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
///     physical existence the gate has already established. The one write that may materialize one is
///     the claim of a restore on a new target name (AB#8672), which a failed restore drops again
///     (<see cref="TryDropIfOnlyClaimAsync" />).
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
    ///     Stamps (or re-stamps) the marker. Used by create, which only writes into a database it
    ///     created itself. Attach claims with <see cref="StampIfAbsentAndGetAsync" /> instead, so two
    ///     instances attaching the same unmarked database cannot both win (AB#8796).
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
    ///     <see cref="StampIfAbsentAsync" /> that also returns the marker as it is afterwards (AB#8796):
    ///     the own marker when this call or an earlier one of this instance wrote it, or the marker of
    ///     another instance that won the insert-if-absent race. One round trip; never overwrites.
    /// </summary>
    /// <param name="databaseName">The database to claim.</param>
    /// <param name="tenantId">The tenant the claim is for.</param>
    /// <param name="ownerSystemDatabaseName">The claiming instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="claimTime">
    ///     The stamp time to write on insert, default now. The restore and the database drop pass a claim token
    ///     (<see cref="NewClaimTime" />): the returned marker is <see cref="IsClaim" /> of it only when THIS call
    ///     inserted it, and only then do they release it again (<see cref="TryRemoveClaimAsync" />).
    /// </param>
    public async Task<TenantOwnershipRecord> StampIfAbsentAndGetAsync(string databaseName, string tenantId,
        string ownerSystemDatabaseName, CancellationToken cancellationToken = default, DateTime? claimTime = null)
    {
        var collection = GetCollection(databaseName);
        var update = Builders<TenantOwnershipRecord>.Update
            .SetOnInsert(r => r.OwnerSystemDatabaseName, ownerSystemDatabaseName)
            .SetOnInsert(r => r.TenantId, tenantId)
            .SetOnInsert(r => r.AttachedAtUtc, claimTime ?? DateTime.UtcNow);

        return await collection.FindOneAndUpdateAsync<TenantOwnershipRecord>(
            r => r.Id == TenantOwnershipRecord.DocumentId, update,
            new FindOneAndUpdateOptions<TenantOwnershipRecord>
            {
                IsUpsert = true, ReturnDocument = ReturnDocument.After
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Claims the database for <paramref name="ownerSystemDatabaseName" /> with a claim token (AB#8796, AB#8672):
    ///     a marker of this instance is re-stamped with <paramref name="tenantId" /> and <paramref name="claimTime" />,
    ///     a missing marker is inserted with them, and the marker of another instance is returned untouched. The
    ///     claim is the caller's exactly when the returned marker is <see cref="IsClaim" /> of it.
    /// </summary>
    /// <remarks>
    ///     The claim always WRITES when it succeeds (also over an own marker), so inside a transaction
    ///     (<paramref name="session" /> with an active transaction) two concurrent claims of the same database
    ///     collide: the second gets a write conflict, and an aborted transaction takes its claim with it. That
    ///     is how the attach ties its claim to its registry commit. Without a session the claim is a plain write
    ///     the caller releases with <see cref="TryRemoveClaimAsync" />.
    /// </remarks>
    public async Task<TenantOwnershipRecord> ClaimAsync(string databaseName, string tenantId,
        string ownerSystemDatabaseName, DateTime claimTime, IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        var restamp = Builders<TenantOwnershipRecord>.Update
            .Set(r => r.TenantId, tenantId)
            .Set(r => r.AttachedAtUtc, claimTime);
        var ownMarker = Builders<TenantOwnershipRecord>.Filter.Where(r =>
            r.Id == TenantOwnershipRecord.DocumentId && r.OwnerSystemDatabaseName == ownerSystemDatabaseName);
        var restamped = session != null
            ? await collection.UpdateOneAsync(session, ownMarker, restamp, cancellationToken: cancellationToken)
                .ConfigureAwait(false)
            : await collection.UpdateOneAsync(ownMarker, restamp, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        if (restamped.MatchedCount == 1)
        {
            return new TenantOwnershipRecord
            {
                OwnerSystemDatabaseName = ownerSystemDatabaseName, TenantId = tenantId, AttachedAtUtc = claimTime
            };
        }

        var insertIfAbsent = Builders<TenantOwnershipRecord>.Update
            .SetOnInsert(r => r.OwnerSystemDatabaseName, ownerSystemDatabaseName)
            .SetOnInsert(r => r.TenantId, tenantId)
            .SetOnInsert(r => r.AttachedAtUtc, claimTime);
        var options = new FindOneAndUpdateOptions<TenantOwnershipRecord>
        {
            IsUpsert = true, ReturnDocument = ReturnDocument.After
        };
        var marker = Builders<TenantOwnershipRecord>.Filter.Where(r => r.Id == TenantOwnershipRecord.DocumentId);
        return session != null
            ? await collection.FindOneAndUpdateAsync(session, marker, insertIfAbsent, options, cancellationToken)
                .ConfigureAwait(false)
            : await collection.FindOneAndUpdateAsync(marker, insertIfAbsent, options, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    ///     The deliberate ownership override of a restore or a delete (AB#8672, AB#8807): replaces exactly the
    ///     foreign marker <paramref name="foreignMarker" /> the guard saw (owner and stamp time) by a claim of this
    ///     instance. False when the marker changed in between - then nobody's claim is overwritten.
    /// </summary>
    public async Task<bool> TryTakeOverMarkerAsync(string databaseName, TenantOwnershipRecord foreignMarker,
        string tenantId, string ownerSystemDatabaseName, DateTime claimTime,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        var expectedOwner = foreignMarker.OwnerSystemDatabaseName;
        var expectedStamp = foreignMarker.AttachedAtUtc;
        var update = Builders<TenantOwnershipRecord>.Update
            .Set(r => r.OwnerSystemDatabaseName, ownerSystemDatabaseName)
            .Set(r => r.TenantId, tenantId)
            .Set(r => r.AttachedAtUtc, claimTime);
        var result = await collection.UpdateOneAsync(
            r => r.Id == TenantOwnershipRecord.DocumentId && r.OwnerSystemDatabaseName == expectedOwner &&
                 r.AttachedAtUtc == expectedStamp, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    ///     Reserves the database for one drop attempt (AB#8807): re-stamps <paramref name="marker" /> for
    ///     <paramref name="ownerSystemDatabaseName" /> with <paramref name="claimTime" /> and a drop lease until
    ///     <paramref name="reservedUntil" />, but only while the marker is unchanged (owner and stamp) and no other
    ///     drop attempt holds an unexpired lease. For a foreign marker the override granted, this is also the takeover.
    ///     Returns false when another attempt holds the database or the marker changed.
    /// </summary>
    public async Task<bool> TryReserveForDropAsync(string databaseName, TenantOwnershipRecord marker,
        string tenantId, string ownerSystemDatabaseName, DateTime claimTime, DateTime reservedUntil,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        var expectedOwner = marker.OwnerSystemDatabaseName;
        var expectedStamp = marker.AttachedAtUtc;
        var filter = Builders<TenantOwnershipRecord>.Filter.And(
            Builders<TenantOwnershipRecord>.Filter.Eq(r => r.Id, TenantOwnershipRecord.DocumentId),
            Builders<TenantOwnershipRecord>.Filter.Eq(r => r.OwnerSystemDatabaseName, expectedOwner),
            Builders<TenantOwnershipRecord>.Filter.Eq(r => r.AttachedAtUtc, expectedStamp),
            Builders<TenantOwnershipRecord>.Filter.Or(
                Builders<TenantOwnershipRecord>.Filter.Eq(r => r.DropReservedUntilUtc, null),
                Builders<TenantOwnershipRecord>.Filter.Lt(r => r.DropReservedUntilUtc, claimTime)));
        var update = Builders<TenantOwnershipRecord>.Update
            .Set(r => r.OwnerSystemDatabaseName, ownerSystemDatabaseName)
            .Set(r => r.TenantId, tenantId)
            .Set(r => r.AttachedAtUtc, claimTime)
            .Set(r => r.DropReservedUntilUtc, reservedUntil);
        var result = await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    /// <summary>
    ///     Whether the database holds nothing but ownership markers (this collection and the system-database
    ///     marker): the shape a claim gives a database it materialized. A missing database counts as empty.
    /// </summary>
    public async Task<bool> HoldsOnlyMarkersAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);
        var collectionNames = await client.ListCollectionNamesAsync(databaseName).ConfigureAwait(false);
        return collectionNames.All(name => name == CollectionName || name == SystemDatabaseMarkerStore.CollectionName);
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
    ///     A stamp time usable as a claim token (AB#8796): UTC, cut to the millisecond precision BSON stores, so
    ///     the value read back equals the value written.
    /// </summary>
    public static DateTime NewClaimTime()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    /// <summary>Whether <paramref name="marker" /> is exactly the claim one call made (see <see cref="ClaimAsync" />).</summary>
    public static bool IsClaim(TenantOwnershipRecord marker, string ownerSystemDatabaseName, string tenantId,
        DateTime claimTime) =>
        string.Equals(marker.OwnerSystemDatabaseName, ownerSystemDatabaseName, StringComparison.Ordinal) &&
        string.Equals(marker.TenantId, tenantId, StringComparison.Ordinal) &&
        marker.AttachedAtUtc.ToUniversalTime() == claimTime;

    /// <summary>
    ///     Releases a claim made without a transaction (AB#8796, AB#8672): removes the marker only while it is still
    ///     exactly that claim (owner, tenant and claim time). A claim another operation - of this or another
    ///     instance - made since is never removed. Best effort.
    /// </summary>
    public async Task TryRemoveClaimAsync(string databaseName, string ownerSystemDatabaseName, string tenantId,
        DateTime claimTime)
    {
        try
        {
            var collection = GetCollection(databaseName);
            await collection.DeleteOneAsync(
                r => r.Id == TenantOwnershipRecord.DocumentId &&
                     r.OwnerSystemDatabaseName == ownerSystemDatabaseName && r.TenantId == tenantId &&
                     r.AttachedAtUtc == claimTime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not release the ownership claim on tenant database '{DatabaseName}'; a stale marker of this " +
                "instance remains (self-heals on the next attach by this instance)", databaseName);
        }
    }

    /// <summary>
    ///     Drops the database when it holds nothing but exactly this claim (AB#8672): the database the claim of a
    ///     restore materialized and the restore then never filled. Returns false - and drops nothing - when anything
    ///     else is in it or the marker is not that claim any more. Best effort.
    /// </summary>
    public async Task<bool> TryDropIfOnlyClaimAsync(string databaseName, string ownerSystemDatabaseName,
        string tenantId, DateTime claimTime)
    {
        try
        {
            var marker = await GetAsync(databaseName).ConfigureAwait(false);
            if (marker == null || !IsClaim(marker, ownerSystemDatabaseName, tenantId, claimTime) ||
                !await HoldsOnlyMarkersAsync(databaseName).ConfigureAwait(false))
            {
                return false;
            }

            await _adminRepositoryAccess.GetRepositoryClient(databaseName).DropRepositoryAsync(databaseName)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not drop database '{DatabaseName}', which holds only the ownership claim of a failed restore",
                databaseName);
            return false;
        }
    }

    /// <summary>
    ///     Removes the marker only when it names <paramref name="ownerSystemDatabaseName" /> (AB#8796).
    ///     Detach uses it: detaching a double-registered tenant in the instance that does NOT own the
    ///     database must leave the owner's marker in place, otherwise the owner would lose its claim.
    ///     A database without a marker stays without one.
    /// </summary>
    public async Task RemoveIfOwnedByAsync(string databaseName, string ownerSystemDatabaseName,
        CancellationToken cancellationToken = default)
    {
        var collection = GetCollection(databaseName);
        await collection.DeleteOneAsync(
            r => r.Id == TenantOwnershipRecord.DocumentId && r.OwnerSystemDatabaseName == ownerSystemDatabaseName,
            cancellationToken).ConfigureAwait(false);
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
