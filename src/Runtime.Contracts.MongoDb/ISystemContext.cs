namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

public interface ISystemContext : ITenantContext
{
    Task<bool> IsSystemTenantExistingAsync();

    Task CreateSystemTenantAsync();
    Task ClearSystemTenantAsync();
    Task DeleteSystemTenantAsync();
}