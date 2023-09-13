using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.Contracts;

namespace Persistence.InternalContracts;

/// <summary>
/// Interface of tenant context for internal access only.
/// </summary>
public interface ITenantContextInternal : ITenantContext
{
    #region Access Management

    /// <summary>
    /// Creates a child tenant context.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContextInternal> GetChildTenantContextInternalAsync(string tenantId);

    /// <summary>
    /// Returns an object that allows access to the tenant repository.
    /// </summary>
    /// <returns></returns>
    Task<ITenantRepositoryInternal> GetTenantRepositoryInternalAsync();

    /// <summary>
    /// Returns an object that allows access to the system tenant repository.
    /// </summary>
    /// <returns></returns>
    Task<ITenantRepositoryInternal> GetSystemTenantRepositoryInternalAsync();
    
    #endregion Access Management     
}