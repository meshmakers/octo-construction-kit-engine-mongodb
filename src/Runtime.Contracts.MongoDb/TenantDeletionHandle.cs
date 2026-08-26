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
public sealed record TenantDeletionHandle(
    string DatabaseName,
    Guid CorrelationId,
    IReadOnlyList<OctoObjectId>? StreamDataArchives = null)
{
    /// <summary>The archives whose stream data tables are dropped with the database; never null.</summary>
    public IReadOnlyList<OctoObjectId> StreamDataArchives { get; init; } = StreamDataArchives ?? [];
}
