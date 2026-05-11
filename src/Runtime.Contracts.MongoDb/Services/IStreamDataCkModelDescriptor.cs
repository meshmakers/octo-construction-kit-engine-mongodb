using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
/// Provides the <see cref="CkModelId"/> of the StreamData CK model (e.g. <c>System.StreamData-1.1.0</c>)
/// that the hosting application has registered. Optional: when not registered the engine treats
/// StreamData as a pure data-plane feature (CrateDB only) and skips CK-model import on
/// <c>EnableStreamDataAsync</c>.
/// </summary>
/// <remarks>
/// Decouples the engine from a hard dependency on the StreamData CK model assembly. Composition
/// roots that ship the model register a concrete descriptor (typically
/// <c>new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId)</c>) so the engine can ask
/// the catalog for the exact version that was compiled into the deployment.
/// </remarks>
public interface IStreamDataCkModelDescriptor
{
    /// <summary>
    /// The exact <see cref="CkModelId"/> (name + version) of the StreamData CK model the host
    /// ships. Compared against the tenant's installed version on
    /// <c>EnableStreamDataAsync</c>; differences trigger an import + auto-bridged migration.
    /// </summary>
    CkModelId CkModelId { get; }
}

/// <summary>
/// Default <see cref="IStreamDataCkModelDescriptor"/> implementation. Composition roots register
/// one of these with the <see cref="CkModelId"/> taken from the generated <c>...CkIds</c> class
/// of the StreamData CK model package.
/// </summary>
public sealed record StreamDataCkModelDescriptor(CkModelId CkModelId) : IStreamDataCkModelDescriptor;
