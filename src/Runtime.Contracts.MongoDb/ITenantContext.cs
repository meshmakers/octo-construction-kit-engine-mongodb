using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Represents a tenant context, that allows the management operations of a tenant.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    ///     Returns the tenant id of the context.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    ///     Gets the system session object
    /// </summary>
    /// <returns></returns>
    Task<IOctoAdminSession> GetAdminSessionAsync();

    #region Access Management

    /// <summary>
    ///     Gets a child tenant context.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContext> GetChildTenantContextAsync(string tenantId);

    /// <summary>
    ///     Gets a child tenant context.
    /// </summary>
    /// <param name="adminSession"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContext> GetChildTenantContextAsync(IOctoAdminSession adminSession, string tenantId);

    /// <summary>
    ///     Returns an object that allows access to the system tenant repository.
    /// </summary>
    /// <returns></returns>
    ITenantRepository GetSystemTenantRepository();


    /// <summary>
    ///     Returns an object that allows access to the system tenant repository as admin.
    /// </summary>
    /// <returns></returns>
    ITenantRepository GetSystemTenantRepositoryAsAdmin();

    /// <summary>
    ///     Returns an object that allows access to the tenant repository.
    /// </summary>
    /// <returns></returns>
    ITenantRepository GetTenantRepository();

    /// <summary>
    /// Returns an object that allows access to the tenant repository as admin.
    /// </summary>
    /// <returns></returns>
    ITenantRepository GetTenantRepositoryAsAdmin();

    /// <summary>
    /// Loads the cache for the tenant.
    /// </summary>
    /// <returns></returns>
    Task LoadCacheForTenantAsync();

    #endregion Access Management

    #region Tenant Management

    Task CreateChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId);

    Task AttachChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId);

    Task DetachChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    Task ClearChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    Task DropChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    /// <summary>
    ///     Returns true if a child tenant with the given name exists.
    /// </summary>
    /// <param name="adminSession"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<bool> IsChildTenantExistingAsync(IOctoAdminSession adminSession, string tenantId);

    Task<IResultSet<OctoTenant>> GetChildTenantsAsync(IOctoAdminSession adminSession, int? skip = null,
        int? take = null);

    Task<OctoTenant> GetChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    #endregion Tenant Management

    #region Configuration

    Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoAdminSession adminSession, string key,
        TValueType? defaultValue) where
        TValueType : class;

    Task<string?> GetConfigurationAsync(IOctoAdminSession adminSession, string key, string? defaultValue = null);

    Task SetConfigurationAsync<TValueType>(IOctoAdminSession adminSession, string key, TValueType value)
        where TValueType : struct;

    Task SetConfigurationAsync(IOctoAdminSession adminSession, string key, string value);

    Task SetConfigurationAsync(IOctoAdminSession adminSession, string key, object value);
    Task DeleteConfigurationAsync(IOctoAdminSession adminSession, string key);

    #endregion Configuration

    #region Construction Kits

    /// <summary>
    ///     Imports a construction kit model into the tenant.
    /// </summary>
    /// <param name="ckCompiledModelRoot"></param>
    /// <returns></returns>
    Task ImportCkModelAsync(CkCompiledModelRoot ckCompiledModelRoot);

    /// <summary>
    ///     Imports a construction kit model into the tenant.
    /// </summary>
    /// <param name="ckModelId">The construction kit model id to load</param>
    /// <param name="operationResult">Object that contains validation messages during load of construction kits</param>
    /// <returns></returns>
    Task ImportCkModelAsync(CkModelId ckModelId, OperationResult operationResult);

    /// <summary>
    ///     Returns true if a construction kit model with the given id exists.
    /// </summary>
    /// <param name="ckModelId">The construction kit model id to check</param>
    /// <returns>True, if the construction kit model exists</returns>
    Task<bool> IsCkModelExistingAsync(CkModelId ckModelId);

    /// <summary>
    ///     Customizes CkEnum values in the repository
    /// </summary>
    /// <param name="ckEnumId">Construction kit enum id</param>
    /// <param name="ckEnumUpdates">Describes the updates to the enum</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns></returns>
    Task CustomizeCkEnumAsync(CkId<CkEnumId> ckEnumId, ICollection<CkEnumUpdate> ckEnumUpdates,
        CancellationToken? cancellationToken = null);

    #endregion
}
