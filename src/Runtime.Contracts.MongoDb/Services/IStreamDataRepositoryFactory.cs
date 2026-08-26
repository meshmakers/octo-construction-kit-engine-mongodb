using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
/// Creates per-tenant <see cref="IStreamDataRepository"/> instances. Implemented by the
/// CrateDB-backed stream data project and registered via
/// <c>AddCrateDbStreamDataRepository&lt;TConfigureOptions&gt;</c>. Decouples the MongoDB-side
/// <c>TenantContext</c> from any concrete StreamData backend.
/// </summary>
public interface IStreamDataRepositoryFactory
{
    /// <summary>
    /// Creates a stream data repository scoped to the given tenant id. The
    /// <paramref name="archiveStore"/> is consulted by the repository to enforce per-archive
    /// status guards (T14) and to resolve the user-defined column list at insert and query time
    /// (T17). The optional <paramref name="rollupArchiveStore"/> is consulted by the chain-aware
    /// aggregation resolver when querying a cascade rollup (rollup-over-rollup) — null when the
    /// tenant has no rollup support configured, in which case cascade chain resolution falls
    /// back to the 1-level resolver. The optional <paramref name="recomputeStateStore"/> lets the
    /// repository record a dirty window when an ingest write lands at/before a dependent's consumed
    /// watermark (AB#4184 retroactive-write detection); null disables that detection.
    /// </summary>
    IStreamDataRepository Create(
        string tenantId,
        IArchiveRuntimeStore archiveStore,
        IRollupArchiveRuntimeStore? rollupArchiveStore = null,
        IArchiveRecomputeStateStore? recomputeStateStore = null);

    /// <summary>
    /// Drops the tables of the given archives of a tenant - the archive table and, for rollups, the
    /// generation-map side-table - each as <c>DROP TABLE IF EXISTS</c>, so archives that never had a
    /// table (Created, or Failed before the DDL) are harmless. Takes only ids so the tenant drop can
    /// call it after the tenant's own database, and with it the archive entities and runtime stores,
    /// is gone (AB#4255).
    /// </summary>
    /// <remarks>
    /// Deliberately per-archive rather than "everything in the tenant's schema": the CrateDB schema
    /// name is derived from the tenant id with <c>-</c> and <c>_</c> stripped, so tenants whose ids
    /// differ only in those characters share one schema, and a schema-wide drop would take another
    /// tenant's tables with it. Stops at the first failure and lets the exception propagate, so an
    /// unreachable CrateDB costs one resilience timeout rather than one per archive; every statement
    /// is idempotent, the caller logs the full list for a manual retry.
    /// </remarks>
    Task DeleteArchiveTablesAsync(string tenantId, IReadOnlyList<OctoObjectId> archiveRtIds);
}
