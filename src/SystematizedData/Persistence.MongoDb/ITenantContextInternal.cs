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
    /// Gets a child tenant context.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContextInternal> GetChildTenantContextInternalAsync(string tenantId);

    #endregion Access Management     
}