using System;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Resolves the archive lifecycle service of a tenant from its tenant context (AB#7997), for the runtime import: a seed
/// that adds a column to an activated archive hands the addition to it. <see cref="ISystemContext" /> is resolved at call
/// time, not in the constructor: the import command is created while the system context is being built (blueprints of
/// service-managed models), so a constructor dependency would close a DI cycle.
/// </summary>
internal sealed class TenantArchiveLifecycleServiceProvider(IServiceProvider serviceProvider)
    : IArchiveLifecycleServiceProvider
{
    /// <inheritdoc />
    public async Task<IArchiveLifecycleService?> GetAsync(string tenantId)
    {
        var systemContext = serviceProvider.GetService<ISystemContext>();
        if (systemContext == null)
        {
            return null;
        }

        var tenantContext = await systemContext.TryFindTenantContextAsync(tenantId);
        return tenantContext?.GetArchiveLifecycleService();
    }
}
