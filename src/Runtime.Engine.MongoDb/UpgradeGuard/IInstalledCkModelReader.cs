using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UpgradeGuard;

/// <summary>
///     The read-only view of a tenant's installed CK models that the upgrade guard needs (AB#6867). The interface has
///     no write member on purpose: the guard cannot write through it.
/// </summary>
internal interface IInstalledCkModelReader
{
    /// <summary>The persisted model rows (id, state, exact pins, dependency ranges with <c>usedSurface</c>), without models being imported.</summary>
    Task<IReadOnlyList<CkModel>> ListInstalledAsync(CancellationToken cancellationToken);

    /// <summary>The persisted model rebuilt as a compiled model; <c>null</c> when it is not installed and <c>Available</c>.</summary>
    Task<CkCompiledModelRoot?> LoadAsync(CkModelId modelId, CancellationToken cancellationToken);
}
