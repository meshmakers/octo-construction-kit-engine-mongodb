using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
/// Marks a host-shipped, service-managed CK model that must be auto-imported and upgraded to its
/// embedded (source-generated) version in every tenant the host resolves — decoupled from any
/// blueprint <c>ckModelDependencies</c> floor. Register one per model; the engine imports each on
/// tenant-resolve with a downgrade guard, so a deploy that ships a newer version auto-propagates to
/// all tenants without a manual or blueprint step. No-op for hosts that register no descriptors.
/// </summary>
/// <remarks>
/// Generalises the existing <see cref="IStreamDataCkModelDescriptor"/> pattern (which additionally
/// gates on a feature flag). Composition roots register
/// <c>new ServiceManagedCkModelDescriptor(&lt;Generated&gt;.SystemUICkIds.CkModelId)</c> so the
/// engine can import the exact version compiled into the deployment.
/// </remarks>
public interface IServiceManagedCkModelDescriptor
{
    /// <summary>
    /// Exact <see cref="CkModelId"/> (name + version) of the service-managed CK model the host ships.
    /// Compared against the tenant's installed version on resolve; a strictly-newer embedded version
    /// triggers an import + auto-bridged migration, an older one is skipped (downgrade guard).
    /// </summary>
    CkModelId CkModelId { get; }
}

/// <summary>
/// Default <see cref="IServiceManagedCkModelDescriptor"/> implementation. Composition roots register
/// one of these with the <see cref="CkModelId"/> taken from the generated <c>...CkIds</c> class of
/// the service-managed CK model package.
/// </summary>
public sealed record ServiceManagedCkModelDescriptor(CkModelId CkModelId)
    : IServiceManagedCkModelDescriptor;
