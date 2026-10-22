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

    /// <summary>
    /// Removes the tenant-level stream data state that is not owned by one archive - the restore reconcile marker and the rollup
    /// repair queue (AB#6968) - when the tenant is deleted for good, so a tenant re-created under the same id starts clean and its
    /// rollups are not blocked by a marker the deleted one left. Idempotent; a default implementation (no such state) does nothing.
    /// </summary>
    /// <remarks>
    /// Deletes the tenant's <b>rows</b> and drops a table only when it is empty afterwards: the CrateDB schema is shared by tenants
    /// whose ids differ only in <c>-</c> and <c>_</c>, so another tenant's rows must survive.
    /// </remarks>
    Task DeleteTenantStateAsync(string tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Asks the storage cluster - not a tenant - whether the native copy of archive data to a staging
    /// location can run: whether the cluster setting that keeps COPY statements out of the job log is
    /// set, and which version the nodes run. The answer is the one
    /// <see cref="IStreamDataRepository.CheckStagingCopyReadinessAsync"/> gives; this member exists so a
    /// caller can ask <em>before</em> it touches the tenant.
    /// </summary>
    /// <remarks>
    /// Needs no tenant context, no CK cache and no <see cref="IArchiveRuntimeStore"/>: a restore has to
    /// know that the archive data can be loaded before it replaces the tenant's database, and building a
    /// tenant context at that point would load the CK cache from the database that is about to be replaced.
    /// Side-effect free (two reads of cluster state, nothing is written or changed) and never throws for a
    /// missing prerequisite - the prerequisite not being met is the answer
    /// (<see cref="StagingCopyReadiness.IsReady"/> is <c>false</c> and
    /// <see cref="StagingCopyReadiness.Reason"/> says what to do). A factory whose storage cannot copy
    /// answers "not supported".
    /// </remarks>
    Task<StagingCopyReadiness> CheckStagingCopyReadinessAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new StagingCopyReadiness(false, false, null,
            "This stream data backend does not support the native copy to a staging location."));

    /// <summary>
    /// Asks the storage cluster whether the archive data of a restore fits on its nodes below the disk watermarks
    /// (<c>cluster.routing.allocation.disk.watermark.high</c>) - before the restore changes anything. The estimate is the
    /// staged (gzip) bytes times a measured size factor, over the copies the tables will have (replicas are switched on again after
    /// the load) and the nodes.
    /// </summary>
    /// <param name="stagedBytes">Sum of the staged files of all archives of the backup.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <remarks>
    /// Side-effect free (reads of the node disks and two cluster settings). Space that the restore frees by replacing tables of the
    /// target tenant is not subtracted. A backend with no disk to ask about does not refuse (<see cref="StagingDiskHeadroom.NotChecked"/>).
    /// </remarks>
    Task<StagingDiskHeadroom> CheckStagingDiskHeadroomAsync(long stagedBytes, CancellationToken cancellationToken = default)
        => Task.FromResult(StagingDiskHeadroom.NotChecked(stagedBytes));
}
