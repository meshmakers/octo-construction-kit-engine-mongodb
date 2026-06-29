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
}
