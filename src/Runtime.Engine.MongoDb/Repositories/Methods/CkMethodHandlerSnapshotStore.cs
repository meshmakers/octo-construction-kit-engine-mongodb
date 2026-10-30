using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Methods;

/// <summary>
///     Default <see cref="ICkMethodHandlerSnapshotStore" />. One document per (method key, instance id) in the non-CK
///     <c>ck_method_handler_snapshot</c> collection of the SYSTEM database (AB#5671), same raw-<c>IMongoDatabase</c>
///     access pattern as <c>TenantSetupRetryStore</c>. A plain collection and not a System CK entity on purpose: it is
///     service bookkeeping with a TTL, not tenant data, and it needs no System model change.
/// </summary>
internal sealed class CkMethodHandlerSnapshotStore : ICkMethodHandlerSnapshotStore
{
    private const string CollectionName = InfrastructureCollections.CkMethodHandlerSnapshot;

    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILogger<CkMethodHandlerSnapshotStore> _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexEnsured;

    private static readonly object ClassMapLock = new();
    private static bool _classMapRegistered;

    public CkMethodHandlerSnapshotStore(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILogger<CkMethodHandlerSnapshotStore> logger)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CkMethodHandlerSnapshotRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection();
        return await collection.Find(FilterDefinition<CkMethodHandlerSnapshotRecord>.Empty)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpsertAsync(IReadOnlyCollection<CkMethodHandlerSnapshotRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return true;
        }

        // createIndexes/an upsert materializes the system database: never before its bootstrap (AB#4854).
        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return false;
        }

        var collection = GetCollection();
        await EnsureIndexesAsync(collection, cancellationToken).ConfigureAwait(false);

        // Replace only a row that is older: a replica that saw less must not overwrite a newer view. The filter
        // misses on a newer-or-equal row, the upsert then collides with the unique index (duplicate key), which is
        // exactly the "keep the newer row" outcome and is ignored.
        var models = records.Select(r => new ReplaceOneModel<CkMethodHandlerSnapshotRecord>(
            Builders<CkMethodHandlerSnapshotRecord>.Filter.And(
                Builders<CkMethodHandlerSnapshotRecord>.Filter.Eq(x => x.MethodKey, r.MethodKey),
                Builders<CkMethodHandlerSnapshotRecord>.Filter.Eq(x => x.InstanceId, r.InstanceId),
                Builders<CkMethodHandlerSnapshotRecord>.Filter.Lt(x => x.LastSeenUtc, r.LastSeenUtc)),
            r) { IsUpsert = true });

        try
        {
            await collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<CkMethodHandlerSnapshotRecord> e)
            when (e.WriteErrors.All(w => w.Category == ServerErrorCategory.DuplicateKey))
        {
            // all conflicts were rows that are already newer in the store
        }

        return true;
    }

    private IMongoCollection<CkMethodHandlerSnapshotRecord> GetCollection()
    {
        var databaseName = _systemContext.DatabaseName;
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);

        // Register the class map only after the repository client has been constructed, so the engine's
        // global camelCase / IgnoreExtraElements conventions are already in place and apply to our AutoMap.
        EnsureClassMapRegistered();

        var repository = (MongoRepository)client.GetRepository(databaseName);
        return repository.Database.GetCollection<CkMethodHandlerSnapshotRecord>(CollectionName);
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

            if (!BsonClassMap.IsClassMapRegistered(typeof(CkMethodHandlerSnapshotRecord)))
            {
                BsonClassMap.RegisterClassMap<CkMethodHandlerSnapshotRecord>(cm =>
                {
                    cm.AutoMap();
                    cm.SetIgnoreExtraElements(true);
                });
            }

            _classMapRegistered = true;
        }
    }

    private async Task EnsureIndexesAsync(IMongoCollection<CkMethodHandlerSnapshotRecord> collection,
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

            await collection.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<CkMethodHandlerSnapshotRecord>(
                    Builders<CkMethodHandlerSnapshotRecord>.IndexKeys
                        .Ascending(r => r.MethodKey)
                        .Ascending(r => r.InstanceId),
                    new CreateIndexOptions { Name = "ck_method_handler_snapshot_key_unique", Unique = true, Background = true }),
                new CreateIndexModel<CkMethodHandlerSnapshotRecord>(
                    Builders<CkMethodHandlerSnapshotRecord>.IndexKeys.Ascending(r => r.RetainUntilUtc),
                    new CreateIndexOptions
                    {
                        Name = "ck_method_handler_snapshot_ttl", ExpireAfter = TimeSpan.Zero, Background = true
                    })
            ], cancellationToken).ConfigureAwait(false);
            _indexEnsured = true;
        }
        catch (Exception ex)
        {
            // The unique key is what keeps two replicas from duplicating a row; without it the store still works
            // (a duplicate row only costs one more entry, the reader merges by key).
            _logger.LogWarning(ex, "Failed to ensure the ck_method_handler_snapshot indexes; continuing without them.");
        }
        finally
        {
            _indexGate.Release();
        }
    }
}
