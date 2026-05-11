using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Default <see cref="IRollupTenantSource"/>: returns the static tenant id list from
/// <see cref="RollupOrchestratorOptions.TenantIds"/>. Suitable for deployments where the set of
/// tenants is known up front (single-tenant pods, environments managed via config). Composition
/// roots with dynamic tenant discovery register their own <see cref="IRollupTenantSource"/>.
/// </summary>
internal sealed class ConfigBasedRollupTenantSource : IRollupTenantSource
{
    private readonly IOptionsMonitor<RollupOrchestratorOptions> _options;

    public ConfigBasedRollupTenantSource(IOptionsMonitor<RollupOrchestratorOptions> options)
    {
        _options = options;
    }

    public Task<IReadOnlyList<string>> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        // IOptionsMonitor surfaces config-reloads at runtime, so adding a tenant id to the
        // configured list takes effect on the next tick without a restart.
        return Task.FromResult(_options.CurrentValue.TenantIds);
    }
}
