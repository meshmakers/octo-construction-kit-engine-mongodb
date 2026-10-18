using Meshmakers.Octo.ConstructionKit.Contracts;

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
