using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantLifecycle;

/// <summary>
/// Default <see cref="ITenantLifecycleStore"/> implementation. Persists one document per tenant in a
/// non-CK <c>tenant_lifecycle</c> collection in the SYSTEM database, resolved through
/// <see cref="ISystemContext"/> + <see cref="IAdminRepositoryAccess"/> — the same raw-<c>IMongoDatabase</c>
/// access pattern as <see cref="IndexUsageService"/>. Kept <c>internal</c> so the engine can swap the
/// resolution path without breaking consumers, who only see <see cref="ITenantLifecycleStore"/> (AB#4348).
/// </summary>
internal sealed class TenantLifecycleStore : ITenantLifecycleStore
{
    private const string CollectionName = "tenant_lifecycle";

    /// <summary>
    /// Stored element names of <see cref="TenantLifecycleRecord"/>, needed by the hand-written
    /// aggregation-pipeline update in <see cref="EnsureCreatingAsync"/> (the typed builders cannot express
    /// <c>$cond</c>). They follow the engine's global camelCase convention; a rename that breaks the mapping
    /// would otherwise fail silently, so <c>TenantLifecycleStoreTests</c> asserts them against the class map.
    /// </summary>
    internal static class Fields
    {
        public const string TenantId = "tenantId";
        public const string DatabaseName = "databaseName";
        public const string CorrelationId = "correlationId";
        public const string State = "state";
        public const string Phase = "phase";
        public const string AttemptCount = "attemptCount";
        public const string LastError = "lastError";
        public const string CreatedUtc = "createdUtc";
        public const string LastTransitionUtc = "lastTransitionUtc";
        public const string LeaseOwner = "leaseOwner";
        public const string LeaseUntil = "leaseUntil";
    }

    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILogger<TenantLifecycleStore> _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexEnsured;

    private static readonly object ClassMapLock = new();
    private static bool _classMapRegistered;

    public TenantLifecycleStore(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILogger<TenantLifecycleStore> logger)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
        _logger = logger;
    }

    public async Task<TenantLifecycleRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        return await collection.Find(Eq(tenantId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantLifecycleRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        return await collection.Find(FilterDefinition<TenantLifecycleRecord>.Empty)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureCreatingAsync(string tenantId, string? databaseName, Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        // Single atomic upsert via an aggregation-pipeline update (AB#4690). This used to be a
        // Find -> build record -> ReplaceOneAsync round trip, which had two defects that together made a
        // stalled tenant unrecoverable:
        //   * the replacement record carried LeaseOwner/LeaseUntil = null, so every repeated setup run
        //     (PosCreateTenant / PosUpdateTenant arrive continuously) wiped the reconciler's lease; and
        //   * AttemptCount was read before and written after a concurrent TryClaimForReconcileAsync,
        //     silently reverting its increment (lost update). The reconciler could therefore never
        //     exhaust its retry budget, never reach Failed, and never record a diagnosable LastError —
        //     it looked frozen at AttemptCount = 0 with no lease.
        // The pipeline below branches on the *stored* state inside the single write, so a concurrent
        // claim is either fully before or fully after it.
        var update = Builders<TenantLifecycleRecord>.Update.Pipeline(
            BuildEnsureCreatingPipeline(databaseName, correlationId, DateTime.UtcNow));

        await collection.UpdateOneAsync(Eq(tenantId), update, new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <see cref="EnsureCreatingAsync"/> pipeline. Three branches, decided on the state that is
    /// already stored:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Active</b> — a healthy tenant re-running setup (every service startup does) must NOT be
    ///     downgraded. Only its metadata is refreshed.
    ///   </item>
    ///   <item>
    ///     <b>Creating</b> — setup for this creation cycle is already in flight. The phase is re-opened,
    ///     but the reconciler's bookkeeping (<c>AttemptCount</c>, <c>LeaseOwner</c>, <c>LeaseUntil</c>) is
    ///     left untouched: it belongs to whoever is currently driving the tenant, not to this caller.
    ///   </item>
    ///   <item>
    ///     <b>Missing / Deleting / Failed</b> — a new creation cycle starts, so the attempt budget, the
    ///     lease and the last error are all reset. A re-created tenant that still carries a stale
    ///     tombstone therefore starts genuinely fresh (the previous implementation inherited the old
    ///     <c>AttemptCount</c>, which contradicted its own comment and shortened the retry budget).
    ///   </item>
    /// </list>
    /// </summary>
    private static PipelineDefinition<TenantLifecycleRecord, TenantLifecycleRecord> BuildEnsureCreatingPipeline(
        string? databaseName, Guid correlationId, DateTime now)
    {
        // On an upsert-insert every field path resolves to "missing", so both comparisons are false and the
        // new-cycle branch applies — which is exactly the semantics we want for a brand-new record.
        var isActive = new BsonDocument("$eq",
            new BsonArray { $"${Fields.State}", (int)TenantLifecycleState.Active });
        var isCreating = new BsonDocument("$eq",
            new BsonArray { $"${Fields.State}", (int)TenantLifecycleState.Creating });
        var keepsReconcileState = new BsonDocument("$or", new BsonArray { isActive, isCreating });

        // Guid.Empty is a legitimate value here (callers that have no correlation id pass it), so the
        // representation must match the class map's Standard-Guid serializer.
        var correlation = new BsonBinaryData(GuidConverter.ToBytes(correlationId, GuidRepresentation.Standard),
            BsonBinarySubType.UuidStandard);

        var databaseNameValue = string.IsNullOrEmpty(databaseName)
            ? (BsonValue)new BsonDocument("$ifNull", new BsonArray { $"${Fields.DatabaseName}", BsonNull.Value })
            : new BsonString(databaseName);

        var set = new BsonDocument
        {
            { Fields.State, Cond(isActive, $"${Fields.State}", (int)TenantLifecycleState.Creating) },
            { Fields.Phase, Cond(isActive, $"${Fields.Phase}", (int)TenantLifecyclePhase.SetupStarted) },
            { Fields.LastError, Cond(isActive, $"${Fields.LastError}", BsonNull.Value) },
            { Fields.CorrelationId, Cond(isActive, $"${Fields.CorrelationId}", correlation) },
            {
                Fields.AttemptCount,
                Cond(keepsReconcileState,
                    new BsonDocument("$ifNull", new BsonArray { $"${Fields.AttemptCount}", 0 }), 0)
            },
            { Fields.LeaseOwner, Cond(keepsReconcileState, $"${Fields.LeaseOwner}", BsonNull.Value) },
            { Fields.LeaseUntil, Cond(keepsReconcileState, $"${Fields.LeaseUntil}", BsonNull.Value) },
            { Fields.CreatedUtc, new BsonDocument("$ifNull", new BsonArray { $"${Fields.CreatedUtc}", now }) },
            { Fields.LastTransitionUtc, now },
            { Fields.DatabaseName, databaseNameValue }
        };

        return new BsonDocumentStagePipelineDefinition<TenantLifecycleRecord, TenantLifecycleRecord>(
            [new BsonDocument("$set", set)]);
    }

    private static BsonDocument Cond(BsonDocument condition, BsonValue whenTrue, BsonValue whenFalse)
        => new("$cond", new BsonArray { condition, whenTrue, whenFalse });

    public async Task SetPhaseAsync(string tenantId, TenantLifecyclePhase phase, string? lastError = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        // Only advance the phase while the tenant is still Creating — never re-open an Active/Deleting tenant.
        var filter = Builders<TenantLifecycleRecord>.Filter.And(
            Eq(tenantId),
            Builders<TenantLifecycleRecord>.Filter.Eq(r => r.State, TenantLifecycleState.Creating));

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.Phase, phase)
            .Set(r => r.LastError, lastError)
            .Set(r => r.LastTransitionUtc, DateTime.UtcNow);

        await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkActiveAsync(string tenantId, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.State, TenantLifecycleState.Active)
            .Set(r => r.Phase, TenantLifecyclePhase.Started)
            .Set(r => r.LastError, (string?)null)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .Set(r => r.LastTransitionUtc, now)
            .SetOnInsert(r => r.CreatedUtc, now)
            .SetOnInsert(r => r.CorrelationId, Guid.Empty);
        if (!string.IsNullOrEmpty(databaseName))
        {
            update = update.Set(r => r.DatabaseName, databaseName);
        }

        await collection.UpdateOneAsync(Eq(tenantId), update, new UpdateOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(string tenantId, string error, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.State, TenantLifecycleState.Failed)
            .Set(r => r.LastError, error)
            .Inc(r => r.AttemptCount, 1)
            .Set(r => r.LastTransitionUtc, DateTime.UtcNow);

        await collection.UpdateOneAsync(Eq(tenantId), update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkDeletingAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        // Update-only (no upsert): a tenant with no lifecycle record (e.g. a legacy tenant created before
        // this feature) needs no tombstone — there is nothing for a concurrent Create to serialize against
        // beyond the existing metadata / database-exists guards. Upserting here would instead leave a
        // phantom Deleting record for a non-existent tenant and permanently 409 any re-create.
        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.State, TenantLifecycleState.Deleting)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .Set(r => r.LastTransitionUtc, DateTime.UtcNow);

        await collection.UpdateOneAsync(Eq(tenantId), update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RemoveAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        await collection.DeleteOneAsync(Eq(tenantId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TenantLifecycleRecord?> TryClaimForReconcileAsync(string leaseOwner, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Claim the longest-waiting Creating tenant whose lease is free (null/missing) or expired. The
        // find-and-update is atomic, so two instances can never claim the same tenant.
        var filter = Builders<TenantLifecycleRecord>.Filter.And(
            Builders<TenantLifecycleRecord>.Filter.Eq(r => r.State, TenantLifecycleState.Creating),
            Builders<TenantLifecycleRecord>.Filter.Or(
                Builders<TenantLifecycleRecord>.Filter.Eq(r => r.LeaseUntil, null),
                Builders<TenantLifecycleRecord>.Filter.Lt(r => r.LeaseUntil, now)));

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.LeaseOwner, leaseOwner)
            .Set(r => r.LeaseUntil, now.Add(leaseDuration))
            .Inc(r => r.AttemptCount, 1)
            .Set(r => r.LastTransitionUtc, now);

        var options = new FindOneAndUpdateOptions<TenantLifecycleRecord>
        {
            ReturnDocument = ReturnDocument.After,
            Sort = Builders<TenantLifecycleRecord>.Sort.Ascending(r => r.LastTransitionUtc)
        };

        return await collection.FindOneAndUpdateAsync(filter, update, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(string tenantId, string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        var filter = Builders<TenantLifecycleRecord>.Filter.And(
            Eq(tenantId),
            Builders<TenantLifecycleRecord>.Filter.Eq(r => r.LeaseOwner, leaseOwner));

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null);

        await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<TenantLifecycleRecord?> RequeueForReconcileAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.State, TenantLifecycleState.Creating)
            .Set(r => r.Phase, TenantLifecyclePhase.SetupStarted)
            .Set(r => r.AttemptCount, 0)
            .Set(r => r.LastError, (string?)null)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .Set(r => r.LastTransitionUtc, DateTime.UtcNow);

        // Update-only (no upsert): returns the reset record, or null when the tenant has no record.
        var options = new FindOneAndUpdateOptions<TenantLifecycleRecord> { ReturnDocument = ReturnDocument.After };
        return await collection.FindOneAndUpdateAsync(Eq(tenantId), update, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private static FilterDefinition<TenantLifecycleRecord> Eq(string tenantId)
        => Builders<TenantLifecycleRecord>.Filter.Eq(r => r.TenantId, tenantId);

    private async Task<IMongoCollection<TenantLifecycleRecord>> GetCollectionAsync(CancellationToken cancellationToken)
    {
        // ISystemContext IS the system tenant context (ISystemContext : ITenantContext), so its
        // DatabaseName is the system database. Resolve the raw IMongoDatabase the same way IndexUsageService
        // does — cast to the concrete MongoRepository, whose Database property is public.
        var databaseName = _systemContext.DatabaseName;
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);

        // Register the class map only after the repository client has been constructed, so the engine's
        // global camelCase / IgnoreExtraElements conventions are already in place and apply to our AutoMap.
        EnsureClassMapRegistered();

        var repository = (MongoRepository)client.GetRepository(databaseName);
        var collection = repository.Database.GetCollection<TenantLifecycleRecord>(CollectionName);

        await EnsureIndexAsync(collection, cancellationToken).ConfigureAwait(false);
        return collection;
    }

    private static void EnsureClassMapRegistered()
    {
        if (_classMapRegistered)
        {
            return;
        }

        lock (ClassMapLock)
        {
            if (_classMapRegistered)
            {
                return;
            }

            if (!BsonClassMap.IsClassMapRegistered(typeof(TenantLifecycleRecord)))
            {
                BsonClassMap.RegisterClassMap<TenantLifecycleRecord>(cm =>
                {
                    cm.AutoMap();
                    cm.SetIgnoreExtraElements(true);
                    // The engine's global Guid serializer is not applied to this non-CK type, so pin the
                    // representation explicitly to Standard (matches the rest of the engine) — otherwise the
                    // driver refuses to serialize a Guid with an Unspecified representation (AB#4348).
                    cm.MapMember(r => r.CorrelationId)
                        .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
                });
            }

            _classMapRegistered = true;
        }
    }

    private async Task EnsureIndexAsync(IMongoCollection<TenantLifecycleRecord> collection,
        CancellationToken cancellationToken)
    {
        if (_indexEnsured)
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexEnsured)
            {
                return;
            }

            var indexModel = new CreateIndexModel<TenantLifecycleRecord>(
                Builders<TenantLifecycleRecord>.IndexKeys.Ascending(r => r.TenantId),
                new CreateIndexOptions { Name = "tenant_lifecycle_tenantId_unique", Unique = true, Background = true });

            await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken).ConfigureAwait(false);
            _indexEnsured = true;
        }
        catch (Exception ex)
        {
            // A transient index-creation failure must not break tenant setup — the store still works without
            // the index (the unique constraint is a safety net, not required for correctness in Phase 1).
            _logger.LogWarning(ex, "Failed to ensure the tenant_lifecycle unique index; continuing without it.");
        }
        finally
        {
            _indexGate.Release();
        }
    }
}
