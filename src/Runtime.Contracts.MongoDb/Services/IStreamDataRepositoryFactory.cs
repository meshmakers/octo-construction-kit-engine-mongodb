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
    /// (T17).
    /// </summary>
    IStreamDataRepository Create(string tenantId, ICkArchiveRuntimeStore archiveStore);
}
