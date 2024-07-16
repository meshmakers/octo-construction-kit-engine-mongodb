using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

public interface ISystemContext : ITenantContext
{
    /// <summary>
    /// Returns true if the system tenant exists.
    /// </summary>
    /// <remarks>
    /// This method checks if the database exists and the system model is available.
    /// </remarks>
    /// <returns></returns>
    Task<bool> IsSystemTenantExistingAsync();

    /// <summary>
    /// Creates the system tenant.
    /// </summary>
    /// <returns></returns>
    Task CreateSystemTenantAsync();
    
    /// <summary>
    /// Clears data of the system tenant.
    /// </summary>
    /// <returns></returns>
    Task ClearSystemTenantAsync();
    
    /// <summary>
    /// Deletes the system tenant.
    /// </summary>
    /// <returns></returns>
    Task DeleteSystemTenantAsync();

    /// <summary>
    /// Gets based on the tenant id the tenant context.
    /// </summary>
    /// <param name="tenantId">The tenant id (also supports the system tenant id)</param>
    /// <returns></returns>
    Task<ITenantContext> FindTenantContextAsync(string tenantId);

    /// <summary>
    /// Gets based on the tenant id the tenant repository.
    /// </summary>
    /// <param name="tenantId">The tenant id (also supports the system tenant id)</param>
    /// <returns></returns>
    Task<ITenantRepository> FindTenantRepositoryAsync(string tenantId);
}