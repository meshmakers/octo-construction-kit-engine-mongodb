using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.DisplayRules;

/// <summary>
///     Default <see cref="IDisplayRuleSweepStore" /> implementation. Persists one document per
///     (tenant, CK type) in a non-CK <c>display_rule_sweep</c> collection in the SYSTEM database,
///     using the same raw-<c>IMongoDatabase</c> access pattern as <c>TenantSetupRetryStore</c>.
/// </summary>
internal sealed class DisplayRuleSweepStore : IDisplayRuleSweepStore
{
    private const string CollectionName = "display_rule_sweep";

    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILogger<DisplayRuleSweepStore> _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexEnsured;

    private static readonly object ClassMapLock = new();
    private static bool _classMapRegistered;

    public DisplayRuleSweepStore(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILogger<DisplayRuleSweepStore> logger)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
        _logger = logger;
    }

    public async Task EnqueueAsync(string tenantId, string ckTypeId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Reset attempts/lease on re-enqueue: a newer rule change must sweep again even if a
        // previous sweep for the same type exhausted its retries.
        var update = Builders<DisplayRuleSweepRecord>.Update
            .Set(r => r.AttemptCount, 0)
            .Set(r => r.LastError, (string?)null)
            .Set(r => r.LastAttemptUtc, DateTime.MinValue)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .SetOnInsert(r => r.EnqueuedUtc, now);

        await collection.UpdateOneAsync(Key(tenantId, ckTypeId), update, new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DisplayRuleSweepRecord?> TryClaimAsync(string leaseOwner, TimeSpan leaseDuration,
        TimeSpan minRetryInterval, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        var filter = Builders<DisplayRuleSweepRecord>.Filter.And(
            // Exhausted entries stay in the collection as a record for operators, but are no longer retried.
            Builders<DisplayRuleSweepRecord>.Filter.Lt(r => r.AttemptCount, maxAttempts),
            Builders<DisplayRuleSweepRecord>.Filter.Lt(r => r.LastAttemptUtc, now - minRetryInterval),
            Builders<DisplayRuleSweepRecord>.Filter.Or(
                Builders<DisplayRuleSweepRecord>.Filter.Eq(r => r.LeaseUntil, null),
                Builders<DisplayRuleSweepRecord>.Filter.Lt(r => r.LeaseUntil, now)));

        var update = Builders<DisplayRuleSweepRecord>.Update
            .Set(r => r.LeaseOwner, leaseOwner)
            .Set(r => r.LeaseUntil, now.Add(leaseDuration));

        var options = new FindOneAndUpdateOptions<DisplayRuleSweepRecord>
        {
            ReturnDocument = ReturnDocument.After,
            Sort = Builders<DisplayRuleSweepRecord>.Sort.Ascending(r => r.LastAttemptUtc)
        };

        return await collection.FindOneAndUpdateAsync(filter, update, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CompleteAsync(string tenantId, string ckTypeId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        await collection.DeleteOneAsync(Key(tenantId, ckTypeId), cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(string tenantId, string ckTypeId, string error,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Releasing the lease here is deliberate: the attempt that just failed is over, so the entry
        // may be picked up again as soon as the retry interval has passed instead of waiting out the lease.
        var update = Builders<DisplayRuleSweepRecord>.Update
            .Set(r => r.LastError, error)
            .Set(r => r.LastAttemptUtc, now)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .Inc(r => r.AttemptCount, 1)
            .SetOnInsert(r => r.EnqueuedUtc, now);

        await collection.UpdateOneAsync(Key(tenantId, ckTypeId), update, new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DisplayRuleSweepRecord>> ListAsync(string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var filter = tenantId is null
            ? FilterDefinition<DisplayRuleSweepRecord>.Empty
            : Builders<DisplayRuleSweepRecord>.Filter.Eq(r => r.TenantId, tenantId);

        return await collection.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FilterDefinition<DisplayRuleSweepRecord> Key(string tenantId, string ckTypeId)
        => Builders<DisplayRuleSweepRecord>.Filter.And(
            Builders<DisplayRuleSweepRecord>.Filter.Eq(r => r.TenantId, tenantId),
            Builders<DisplayRuleSweepRecord>.Filter.Eq(r => r.CkTypeId, ckTypeId));

    private async Task<IMongoCollection<DisplayRuleSweepRecord>> GetCollectionAsync(
        CancellationToken cancellationToken)
    {
        var databaseName = _systemContext.DatabaseName;
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);

        // Register the class map only after the repository client has been constructed, so the engine's
        // global camelCase / IgnoreExtraElements conventions are already in place and apply to our AutoMap.
        EnsureClassMapRegistered();

        var repository = (MongoRepository)client.GetRepository(databaseName);
        var collection = repository.Database.GetCollection<DisplayRuleSweepRecord>(CollectionName);

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

            if (!BsonClassMap.IsClassMapRegistered(typeof(DisplayRuleSweepRecord)))
            {
                BsonClassMap.RegisterClassMap<DisplayRuleSweepRecord>(cm =>
                {
                    cm.AutoMap();
                    cm.SetIgnoreExtraElements(true);
                });
            }

            _classMapRegistered = true;
        }
    }

    private async Task EnsureIndexAsync(IMongoCollection<DisplayRuleSweepRecord> collection,
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

            var indexModel = new CreateIndexModel<DisplayRuleSweepRecord>(
                Builders<DisplayRuleSweepRecord>.IndexKeys
                    .Ascending(r => r.TenantId)
                    .Ascending(r => r.CkTypeId),
                new CreateIndexOptions
                {
                    Name = "display_rule_sweep_tenant_type_unique", Unique = true, Background = true
                });

            await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _indexEnsured = true;
        }
        catch (Exception ex)
        {
            // A transient index-creation failure must not break the sweep — the store still works
            // without the index (the unique constraint is a safety net, not required for correctness).
            _logger.LogWarning(ex, "Failed to ensure the display_rule_sweep unique index; continuing without it.");
        }
        finally
        {
            _indexGate.Release();
        }
    }
}
