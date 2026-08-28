using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     Reads and writes the per-tenant-database ownership marker (AB#4945) — the cross-instance
///     complement of the tenant namespace gate (AB#4762/AB#4763): the gate's registry lookups
///     only cover the OWN instance's system database, so on a shared MongoDB server a second
///     instance could attach a tenant database the first instance still owns. The marker lives in
///     the tenant database itself and is therefore visible to every instance.
/// </summary>
/// <remarks>
///     The collection is deliberately NOT in <c>InfrastructureCollections</c>: that registry is
///     the system-database shell allowlist (AB#4854), and this marker is never written into a
///     system database (the namespace gate reserves the system database name before any marker
///     access). Reads never materialize a database — every caller operates on a database whose
///     physical existence the gate has already established.
/// </remarks>
internal sealed class TenantOwnershipStore
{
    internal const string CollectionName = "tenant_ownership";

    private static readonly object ClassMapLock = new();
    private static bool _classMapRegistered;

    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILogger<TenantOwnershipStore> _logger;

    public TenantOwnershipStore(IAdminRepositoryAccess adminRepositoryAccess, ILogger<TenantOwnershipStore> logger)
    {
        _adminRepositoryAccess = adminRepositoryAccess;
        _logger = logger;
    }

    /// <summary>Returns the marker of the given tenant database, or null when unstamped (legacy).</summary>
    public async Task<TenantOwnershipRecord?> GetAsync(string databaseName,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        return await collection.Find(r => r.Id == TenantOwnershipRecord.DocumentId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stamps (or re-stamps) the marker. Used by create and attach, where the namespace gate
    ///     has already established that any existing marker belongs to this instance.
    /// </summary>
    public async Task StampAsync(string databaseName, string tenantId, string ownerSystemDatabaseName,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        var update = Builders<TenantOwnershipRecord>.Update
            .Set(r => r.OwnerSystemDatabaseName, ownerSystemDatabaseName)
            .Set(r => r.TenantId, tenantId)
            .Set(r => r.AttachedAtUtc, DateTime.UtcNow);

        await collection.UpdateOneAsync(r => r.Id == TenantOwnershipRecord.DocumentId, update,
            new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stamps the marker only when none exists — the lazy claim on tenant-resolve that lets an
    ///     existing fleet (attached before the marker shipped) become owned without a migration
    ///     script. Insert-if-absent semantics: when two instances hold a pre-guard double
    ///     attachment of the same database, the first writer wins and the marker never flaps.
    /// </summary>
    public async Task StampIfAbsentAsync(string databaseName, string tenantId, string ownerSystemDatabaseName,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        var update = Builders<TenantOwnershipRecord>.Update
            .SetOnInsert(r => r.OwnerSystemDatabaseName, ownerSystemDatabaseName)
            .SetOnInsert(r => r.TenantId, tenantId)
            .SetOnInsert(r => r.AttachedAtUtc, DateTime.UtcNow);

        await collection.UpdateOneAsync(r => r.Id == TenantOwnershipRecord.DocumentId, update,
            new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes the marker. Detach calls this LAST so a failed detach leaves the database
    ///     consistently attached (registry rows roll back with the caller's transaction); after a
    ///     successful removal the database is attachable by any instance again — detach is the one
    ///     sanctioned ownership handover (strict guard, no force override).
    /// </summary>
    public async Task RemoveAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        await collection.DeleteOneAsync(r => r.Id == TenantOwnershipRecord.DocumentId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Best-effort removal for rollback paths (a failed attach must not leave a fresh marker
    ///     behind that would lock the database for other instances).
    /// </summary>
    public async Task TryRemoveAsync(string databaseName)
    {
        try
        {
            await RemoveAsync(databaseName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not remove the ownership marker from tenant database '{DatabaseName}' during rollback; " +
                "a stale marker of this instance remains (self-heals on the next attach by this instance)",
                databaseName);
        }
    }

    private IMongoCollection<TenantOwnershipRecord> GetCollection(string databaseName)
    {
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);

        // Register the class map only after the repository client has been constructed, so the engine's
        // global camelCase / IgnoreExtraElements conventions are already in place (same pattern as
        // TenantSetupRetryStore).
        EnsureClassMapRegistered();

        var repository = (MongoRepository)client.GetRepository(databaseName);
        return repository.Database.GetCollection<TenantOwnershipRecord>(CollectionName);
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

            if (!BsonClassMap.IsClassMapRegistered(typeof(TenantOwnershipRecord)))
            {
                BsonClassMap.RegisterClassMap<TenantOwnershipRecord>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(c => c.Id);
                    cm.SetIgnoreExtraElements(true);
                });
            }

            _classMapRegistered = true;
        }
    }
}
