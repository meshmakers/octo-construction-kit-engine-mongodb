namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
///     Interface for tenant notifications
/// </summary>
public interface ITenantNotifications
{
    /// <summary>
    ///     Notify that a tenant will be created
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="correlationId">Grouping id for the operation</param>
    /// <returns></returns>
    Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId);

    /// <summary>
    ///     Notify that a tenant has been created
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="correlationId">Grouping id for the operation</param>
    /// <returns></returns>
    Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId);

    /// <summary>
    ///     Notify that a tenant will be updated
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="correlationId">Grouping id for the operation</param>
    /// <returns></returns>
    Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId);

    /// <summary>
    ///     Notify that a tenant has been updated
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="correlationId">Grouping id for the operation</param>
    /// <returns></returns>
    Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId);

    /// <summary>
    ///     Notify that a tenant will be deleted
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="correlationId">Grouping id for the operation</param>
    /// <returns></returns>
    Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId);

    /// <summary>
    ///     Notify that a tenant has been deleted
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="correlationId">Grouping id for the operation</param>
    /// <returns></returns>
    Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId);
}