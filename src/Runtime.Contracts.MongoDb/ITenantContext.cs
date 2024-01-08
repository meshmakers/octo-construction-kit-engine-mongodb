using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
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
    Task<IOctoSystemSession> GetSystemSessionAsync();

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
    /// <param name="systemSession"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<ITenantContext> GetChildTenantContextAsync(IOctoSystemSession systemSession, string tenantId);

    /// <summary>
    ///     Returns an object that allows access to the system tenant repository.
    /// </summary>
    /// <returns></returns>
    ITenantRepository GetSystemTenantRepository();

    /// <summary>
    ///     Returns an object that allows access to the tenant repository.
    /// </summary>
    /// <returns></returns>
    ITenantRepository GetTenantRepository();

    /// <summary>
    /// Loads the cache for the tenant.
    /// </summary>
    /// <returns></returns>
    Task LoadCacheForTenantAsync();

    #endregion Access Management

    #region Tenant Management

    Task CreateChildTenantAsync(IOctoSystemSession systemSession, string databaseName, string tenantId);

    Task AttachChildTenantAsync(IOctoSystemSession systemSession, string databaseName, string tenantId);

    Task DetachChildTenantAsync(IOctoSystemSession systemSession, string tenantId);

    Task ClearChildTenantAsync(IOctoSystemSession systemSession, string tenantId);

    Task DropChildTenantAsync(IOctoSystemSession systemSession, string tenantId);

    /// <summary>
    ///     Returns true if a child tenant with the given name exists.
    /// </summary>
    /// <param name="systemSession"></param>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<bool> IsChildTenantExistingAsync(IOctoSystemSession systemSession, string tenantId);

    Task<IResultSet<OctoTenant>> GetChildTenantsAsync(IOctoSystemSession systemSession, int? skip = null,
        int? take = null);

    Task<OctoTenant> GetChildTenantAsync(IOctoSystemSession systemSession, string tenantId);

    #endregion Tenant Management

    #region Configuration

    Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoSystemSession systemSession, string key,
        TValueType? defaultValue) where
        TValueType : class;

    Task<string?> GetConfigurationAsync(IOctoSystemSession systemSession, string key, string? defaultValue = null);

    Task SetConfigurationAsync<TValueType>(IOctoSystemSession systemSession, string key, TValueType value)
        where TValueType : struct;

    Task SetConfigurationAsync(IOctoSystemSession systemSession, string key, string value);

    Task SetConfigurationAsync(IOctoSystemSession systemSession, string key, object value);

    #endregion Configuration

    #region Construction Kits

    /// <summary>
    ///     Imports a construction kit model into the tenant.
    /// </summary>
    /// <param name="systemSession"></param>
    /// <param name="ckCompiledModelRoot"></param>
    /// <returns></returns>
    Task ImportCkModelAsync(IOctoSystemSession systemSession, CkCompiledModelRoot ckCompiledModelRoot);

    /// <summary>
    ///     Imports a construction kit model into the tenant.
    /// </summary>
    /// <param name="systemSession">The system session object</param>
    /// <param name="ckModelId">The construction kit model id to load</param>
    /// <param name="operationResult">Object that contains validation messages during load of construction kits</param>
    /// <returns></returns>
    Task ImportCkModelAsync(IOctoSystemSession systemSession, CkModelId ckModelId, OperationResult operationResult);

    /// <summary>
    ///     Returns true if a construction kit model with the given id exists.
    /// </summary>
    /// <param name="systemSession">The system session object</param>
    /// <param name="ckModelId">The construction kit model id to check</param>
    /// <returns>True, if the construction kit model exists</returns>
    Task<bool> IsCkModelExistingAsync(IOctoSystemSession systemSession, CkModelId ckModelId);

    #endregion
}