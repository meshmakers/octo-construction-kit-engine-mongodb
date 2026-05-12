using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Dynamic <see cref="IRollupTenantSource"/> for multi-tenant deployments: enumerates every
/// non-system tenant registered in the system tenant's <c>RtTenant</c> collection on each tick.
/// The per-tenant orchestrator (<see cref="ITenantContext.GetRollupOrchestrator"/>) is null when
/// StreamData is disabled for a tenant, so this source intentionally does not filter — it returns
/// the full population and lets <see cref="RollupOrchestratorHostedService"/> skip the disabled
/// ones.
/// </summary>
/// <remarks>
/// Asset-repo registers this in place of <see cref="ConfigBasedRollupTenantSource"/> so operators
/// don't have to maintain a static tenant list in appsettings. The config-based variant remains
/// the default for single-tenant pods or test fixtures that pin a known tenant set.
/// </remarks>
public sealed class SystemContextRollupTenantSource : IRollupTenantSource
{
    private readonly ISystemContext _systemContext;
    private readonly ILogger<SystemContextRollupTenantSource> _logger;

    public SystemContextRollupTenantSource(
        ISystemContext systemContext,
        ILogger<SystemContextRollupTenantSource> logger)
    {
        _systemContext = systemContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var adminSession = await _systemContext.GetAdminSessionAsync();
            var resultSet = await _systemContext.GetChildTenantsAsync(adminSession);
            var ids = new List<string>(resultSet.Items.Count());
            foreach (var tenant in resultSet.Items)
            {
                ids.Add(tenant.TenantId);
            }
            return ids;
        }
        catch (System.Exception ex)
        {
            // Don't kill the orchestrator tick on a transient enumeration failure — log and return
            // empty so the host service skips this tick and retries on the next interval.
            _logger.LogWarning(ex,
                "Failed to enumerate tenants for the rollup orchestrator; skipping this tick.");
            return System.Array.Empty<string>();
        }
    }
}
