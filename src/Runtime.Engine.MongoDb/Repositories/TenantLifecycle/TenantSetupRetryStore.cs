using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantLifecycle;

/// <summary>
/// Default <see cref="ITenantSetupRetryStore"/> implementation. Persists one document per
/// (service, tenant) in a non-CK <c>tenant_setup_retry</c> collection in the SYSTEM database, using the
/// same raw-<c>IMongoDatabase</c> access pattern as <see cref="TenantLifecycleStore"/> (AB#4690).
/// </summary>
internal sealed class TenantSetupRetryStore : ITenantSetupRetryStore
{
    private const string CollectionName = "tenant_setup_retry";

    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILogger<TenantSetupRetryStore> _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexEnsured;

    private static readonly object ClassMapLock = new();
    private static bool _classMapRegistered;

    public TenantSetupRetryStore(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILogger<TenantSetupRetryStore> logger)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
        _logger = logger;
    }

    public async Task RecordFailureAsync(string serviceId, string tenantId, string error,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        // Releasing the lease here is deliberate: the attempt that just failed is over, so the entry may be
        // picked up again as soon as the retry interval has passed instead of waiting out the lease.
        var update = Builders<TenantSetupRetryRecord>.Update
            .Set(r => r.LastError, error)
            .Set(r => r.LastAttemptUtc, now)
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null)
            .Inc(r => r.AttemptCount, 1)
            .SetOnInsert(r => r.FirstFailureUtc, now);

        await collection.UpdateOneAsync(Key(serviceId, tenantId), update, new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(string serviceId, string tenantId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        await collection.DeleteOneAsync(Key(serviceId, tenantId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> ClearAllForTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var result = await collection
            .DeleteManyAsync(Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.TenantId, tenantId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.DeletedCount;
    }

    public async Task<TenantSetupRetryRecord?> TryClaimAsync(string serviceId, string leaseOwner,
        TimeSpan leaseDuration, TimeSpan minRetryInterval, int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        var filter = Builders<TenantSetupRetryRecord>.Filter.And(
            Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.ServiceId, serviceId),
            // Exhausted entries stay in the collection as a record for operators, but are no longer retried.
            Builders<TenantSetupRetryRecord>.Filter.Lt(r => r.AttemptCount, maxAttempts),
            Builders<TenantSetupRetryRecord>.Filter.Lt(r => r.LastAttemptUtc, now - minRetryInterval),
            Builders<TenantSetupRetryRecord>.Filter.Or(
                Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.LeaseUntil, null),
                Builders<TenantSetupRetryRecord>.Filter.Lt(r => r.LeaseUntil, now)));

        var update = Builders<TenantSetupRetryRecord>.Update
            .Set(r => r.LeaseOwner, leaseOwner)
            .Set(r => r.LeaseUntil, now.Add(leaseDuration));

        var options = new FindOneAndUpdateOptions<TenantSetupRetryRecord>
        {
            ReturnDocument = ReturnDocument.After,
            Sort = Builders<TenantSetupRetryRecord>.Sort.Ascending(r => r.LastAttemptUtc)
        };

        return await collection.FindOneAndUpdateAsync(filter, update, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReleaseLeaseAsync(string serviceId, string tenantId, string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);

        var filter = Builders<TenantSetupRetryRecord>.Filter.And(
            Key(serviceId, tenantId),
            Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.LeaseOwner, leaseOwner));

        var update = Builders<TenantSetupRetryRecord>.Update
            .Set(r => r.LeaseOwner, (string?)null)
            .Set(r => r.LeaseUntil, (DateTime?)null);

        await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantSetupRetryRecord>> ListAsync(string? serviceId = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var filter = serviceId is null
            ? FilterDefinition<TenantSetupRetryRecord>.Empty
            : Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.ServiceId, serviceId);

        return await collection.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FilterDefinition<TenantSetupRetryRecord> Key(string serviceId, string tenantId)
        => Builders<TenantSetupRetryRecord>.Filter.And(
            Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.ServiceId, serviceId),
            Builders<TenantSetupRetryRecord>.Filter.Eq(r => r.TenantId, tenantId));

    private async Task<IMongoCollection<TenantSetupRetryRecord>> GetCollectionAsync(
        CancellationToken cancellationToken)
    {
        var databaseName = _systemContext.DatabaseName;
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);

        // Register the class map only after the repository client has been constructed, so the engine's
        // global camelCase / IgnoreExtraElements conventions are already in place and apply to our AutoMap.
        EnsureClassMapRegistered();

        var repository = (MongoRepository)client.GetRepository(databaseName);
        var collection = repository.Database.GetCollection<TenantSetupRetryRecord>(CollectionName);

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

            if (!BsonClassMap.IsClassMapRegistered(typeof(TenantSetupRetryRecord)))
            {
                BsonClassMap.RegisterClassMap<TenantSetupRetryRecord>(cm =>
                {
                    cm.AutoMap();
                    cm.SetIgnoreExtraElements(true);
                });
            }

            _classMapRegistered = true;
        }
    }

    private async Task EnsureIndexAsync(IMongoCollection<TenantSetupRetryRecord> collection,
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

            var indexModel = new CreateIndexModel<TenantSetupRetryRecord>(
                Builders<TenantSetupRetryRecord>.IndexKeys
                    .Ascending(r => r.ServiceId)
                    .Ascending(r => r.TenantId),
                new CreateIndexOptions
                {
                    Name = "tenant_setup_retry_service_tenant_unique", Unique = true, Background = true
                });

            await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _indexEnsured = true;
        }
        catch (Exception ex)
        {
            // A transient index-creation failure must not break tenant setup — the store still works without
            // the index (the unique constraint is a safety net, not required for correctness).
            _logger.LogWarning(ex, "Failed to ensure the tenant_setup_retry unique index; continuing without it.");
        }
        finally
        {
            _indexGate.Release();
        }
    }
}
