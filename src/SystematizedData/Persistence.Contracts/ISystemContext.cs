using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface ISystemContext : ITenantContext
{
    Task<bool> IsSystemTenantExistingAsync();

    Task CreateSystemTenantAsync();
    Task ClearSystemTenantAsync();
    Task DeleteSystemTenantAsync();

}
