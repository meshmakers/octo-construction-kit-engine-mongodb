using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface ISystemContext : ITenantContext
{
    Task<bool> IsSystemDatabaseExistingAsync();

    Task CreateSystemDatabaseAsync();
    Task ClearSystemDatabaseAsync();
    Task DropSystemDatabaseAsync();

}
