namespace Meshmakers.Octo.Common.DistributedCache.DistributedOperations;

/// <summary>
/// Base class for arguments of operations
/// </summary>
public abstract record ArgumentBase
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id if null the system tenant is used.</param>
    protected ArgumentBase(string? tenantId)
    {
        TenantId = tenantId;
    }
    
    /// <summary>
    /// Returns the tenant id
    /// </summary>
    public string? TenantId { get; }
}