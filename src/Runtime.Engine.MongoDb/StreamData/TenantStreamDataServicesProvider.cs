using System;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Resolves the stream-data services of a tenant from its tenant context (AB#7991), for the blueprint engine: after a
/// seed created new archives it activates them and queues the backfill of new rollups. <see cref="ISystemContext" /> is
/// resolved at call time, not in the constructor, for the same reason as in <see cref="TenantArchiveLifecycleServiceProvider" />:
/// the blueprint service is created while the system context is being built (service-managed blueprints).
/// </summary>
internal sealed class TenantStreamDataServicesProvider(IServiceProvider serviceProvider)
    : IStreamDataTenantServicesProvider
{
    /// <inheritdoc />
    public async Task<StreamDataTenantServices?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var systemContext = serviceProvider.GetService<ISystemContext>();
        if (systemContext == null)
        {
            return null;
        }

        var tenantContext = await systemContext.TryFindTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            return null;
        }

        var lifecycle = tenantContext.GetArchiveLifecycleService();
        if (lifecycle == null)
        {
            // No stream-data backend registered in this host.
            return null;
        }

        var enabled = await tenantContext.IsStreamDataEnabledAsync();
        return new StreamDataTenantServices(
            enabled,
            lifecycle,
            tenantContext.GetArchiveRuntimeStore(),
            tenantContext.GetRollupArchiveRuntimeStore(),
            enabled ? tenantContext.GetRecomputeOrchestrator() : null);
    }
}
