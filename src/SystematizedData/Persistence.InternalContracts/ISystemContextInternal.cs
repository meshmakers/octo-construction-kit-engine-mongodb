using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Persistence.InternalContracts;

public interface ISystemContextInternal : ITenantContextInternal, ISystemContext
{
    /// <summary>
    /// Creates a tenant context.
    /// </summary>
    /// <param name="session"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContextInternal> GetTenantContextInternalAsync(IOctoSession session, string tenantId);
}