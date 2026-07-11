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
        var now = DateTime.UtcNow;
        var filter = Eq(tenantId);
        var existing = await collection.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // A healthy tenant re-running setup (e.g. on every service startup) must NOT be downgraded to
        // Creating — just refresh its metadata. Missing / Creating / Deleting / Failed records are (re)set
        // to Creating, so a re-created tenant that still carries a stale tombstone starts fresh.
        if (existing is { State: TenantLifecycleState.Active })
        {
            var touch = Builders<TenantLifecycleRecord>.Update.Set(r => r.LastTransitionUtc, now);
            if (!string.IsNullOrEmpty(databaseName))
            {
                touch = touch.Set(r => r.DatabaseName, databaseName);
            }

            await collection.UpdateOneAsync(filter, touch, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var record = new TenantLifecycleRecord
        {
            TenantId = tenantId,
            DatabaseName = databaseName ?? existing?.DatabaseName,
            CorrelationId = correlationId,
            State = TenantLifecycleState.Creating,
            Phase = TenantLifecyclePhase.SetupStarted,
            AttemptCount = existing?.AttemptCount ?? 0,
            LastError = null,
            CreatedUtc = existing?.CreatedUtc ?? now,
            LastTransitionUtc = now
        };

        await collection.ReplaceOneAsync(filter, record, new ReplaceOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
    }

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
        var now = DateTime.UtcNow;

        var update = Builders<TenantLifecycleRecord>.Update
            .Set(r => r.State, TenantLifecycleState.Deleting)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .Set(r => r.LastTransitionUtc, now)
            .SetOnInsert(r => r.CreatedUtc, now)
            .SetOnInsert(r => r.CorrelationId, Guid.Empty);

        await collection.UpdateOneAsync(Eq(tenantId), update, new UpdateOptions { IsUpsert = true }, cancellationToken)
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
