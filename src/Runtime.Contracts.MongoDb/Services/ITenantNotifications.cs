namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
///     Interface for tenant notifications
/// </summary>
public interface ITenantNotifications
{
    /// <summary>
    ///     Notify that a tenant will be created
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task NotifyPreTenantCreateAsync(string tenantId);

    /// <summary>
    ///     Notify that a tenant has been created
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task NotifyPosTenantCreateAsync(string tenantId);

    /// <summary>
    ///     Notify that a tenant will be updated
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task NotifyPreTenantUpdateAsync(string tenantId);

    /// <summary>
    ///     Notify that a tenant has been updated
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task NotifyPosTenantUpdateAsync(string tenantId);

    /// <summary>
    ///     Notify that a tenant will be deleted
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task NotifyPreTenantDeleteAsync(string tenantId);

    /// <summary>
    ///     Notify that a tenant has been deleted
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task NotifyPosTenantDeleteAsync(string tenantId);
}