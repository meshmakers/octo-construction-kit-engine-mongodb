using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Carries the state required to drop a tenant's physical database <b>after</b> its metadata
///     records have been deleted and committed.
/// </summary>
/// <remarks>
///     The two-phase delete (delete metadata → commit → drop database) closes a race in which a
///     concurrent tenant-resolve re-creates the tenant database via CK-model auto-import while the
///     tenant record is still visible to other sessions. See
///     <see cref="ITenantContext.DeleteChildTenantMetadataAsync" /> and
///     <see cref="ITenantContext.DropTenantDatabaseAsync" />.
/// </remarks>
/// <param name="DatabaseName">The physical database name of the tenant being deleted.</param>
/// <param name="CorrelationId">
///     The correlation id shared between the pre-delete notification (raised during metadata
///     deletion) and the post-delete notification (raised after the physical database drop).
/// </param>
/// <param name="StreamDataArchives">
///     The archives whose stream data (CrateDB) tables are dropped together with the database
///     (AB#4255). Collected by the metadata deletion while the tenant is still resolvable - the
///     entities are gone with the database, so the drop phase cannot enumerate them itself. Empty
///     when the caller did not ask for the stream data to be dropped (a database swap such as a
///     restore, where the same archives continue to exist afterwards), when the tenant has no
///     archives, or for a handle built from a lifecycle record after the fact.
/// </param>
/// <param name="StreamDataModelPresent">
///     True when the tenant had the System.StreamData model loaded when the metadata was deleted (it may still have no archive). The
///     tenant-level stream data state (restore reconcile marker, rollup repair queue; AB#6968) is removed with the database only
///     then, so a tenant that never used stream data does not touch CrateDB on delete.
/// </param>
public sealed record TenantDeletionHandle(
    string DatabaseName,
    Guid CorrelationId,
    IReadOnlyList<OctoObjectId> StreamDataArchives,
    bool StreamDataModelPresent = false)
{
    /// <summary>
    ///     The ownership marker of another OctoMesh instance that a deliberate administrator override let the metadata
    ///     deletion go past (an orphaned foreign marker, AB#8807); null when no override was granted.
    ///     <see cref="ITenantContext.DropTenantDatabaseAsync" /> re-checks the cross-instance ownership right before
    ///     every drop and honours the override only while exactly this marker is still on the database (or the marker
    ///     this instance took it over with): a database another instance re-created under the same name carries a new
    ///     marker and is never dropped. The delete endpoint keeps the identity in the delete's tombstone
    ///     (<see cref="TenantLifecycle.TenantLifecycleRecord.OverriddenForeignMarkerOwner" />) before it commits the
    ///     metadata deletion, so the settle sweep, which builds its handle from the tombstone, finishes an interrupted
    ///     delete with the same decision.
    /// </summary>
    public TenantOwnershipMarkerIdentity? OverriddenForeignMarker { get; init; }

    /// <summary>
    ///     True when the metadata deletion granted the ownership override (<see cref="OverriddenForeignMarker" />).
    /// </summary>
    public bool ForeignOwnershipOverridden => OverriddenForeignMarker != null;

    /// <summary>A handle for a plain database drop: no stream data tables go with the database.</summary>
    public TenantDeletionHandle(string databaseName, Guid correlationId)
        : this(databaseName, correlationId, [])
    {
    }

    /// <summary>
    ///     Binary compatibility: the shape before <see cref="StreamDataModelPresent" /> existed. A package compiled against the
    ///     earlier engine-mongodb keeps working next to this one (pipeline builds float on the main line, so such a mix can be
    ///     loaded); without this constructor and the matching <c>Deconstruct</c> the call fails with a MissingMethodException.
    /// </summary>
    public TenantDeletionHandle(string databaseName, Guid correlationId, IReadOnlyList<OctoObjectId> streamDataArchives)
        : this(databaseName, correlationId, streamDataArchives, false)
    {
    }

    /// <summary>Binary compatibility with consumers compiled before <see cref="StreamDataModelPresent" /> existed.</summary>
    public void Deconstruct(
        out string databaseName, out Guid correlationId, out IReadOnlyList<OctoObjectId> streamDataArchives)
    {
        databaseName = DatabaseName;
        correlationId = CorrelationId;
        streamDataArchives = StreamDataArchives;
    }
}
