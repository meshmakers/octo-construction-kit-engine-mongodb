using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface ISystemContext : ITenantContext
{
    Task<bool> IsSystemTenantExistingAsync();

    Task CreateSystemTenantAsync();
    Task ClearSystemTenantAsync();
    Task DeleteSystemTenantAsync();

    /// <summary>
    /// Creates a tenant context.
    /// </summary>
    /// <param name="session"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContext> GetTenantContextAsync(IOctoSession session, string tenantId);
    
    /// <summary>
    /// Returns true if a tenant is existing. It is check if a tenant is existing for another tenant too.
    /// </summary>
    /// <param name="systemSession"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<bool> IsTenantExistingAsync(IOctoSystemSession systemSession, string tenantId);
}
