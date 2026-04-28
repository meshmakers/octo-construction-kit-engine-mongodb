using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

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
    /// Returns the database name of the tenant context.
    /// </summary>
    string DatabaseName { get; }

    /// <summary>
    ///     Gets the system session object
    /// </summary>
    /// <returns></returns>
    Task<IOctoAdminSession> GetAdminSessionAsync();

    #region Access Management

    /// <summary>
    ///     Gets a child tenant context.
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>Tenant context</returns>
    Task<ITenantContext> GetChildTenantContextAsync(string tenantId);

    /// <summary>
    ///     Tries to get a child tenant context.
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>Tenant context or null if not found</returns>
    Task<ITenantContext?> TryGetChildTenantContextAsync(string tenantId);

    /// <summary>
    ///     Gets a child tenant context.
    /// </summary>
    /// <param name="adminSession">Admin session to get the tenant context</param>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>Tenant context</returns>
    Task<ITenantContext> GetChildTenantContextAsync(IOctoAdminSession adminSession, string tenantId);

    /// <summary>
    ///     Tries to get a child tenant context.
    /// </summary>
    /// <param name="adminSession">Admin session to get the tenant context</param>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>Tenant context or null if not found</returns>
    Task<ITenantContext?> TryGetChildTenantContextAsync(IOctoAdminSession adminSession, string tenantId);

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

    /// <summary>
    /// Returns the stream data repository for this tenant, or null if stream data is not enabled.
    /// </summary>
    IStreamDataRepository? GetStreamDataRepository();

    /// <summary>
    /// Returns the archive runtime store for this tenant. Reads and writes <c>CkArchive</c>
    /// entities through MongoDB. Used by the archive lifecycle service (concept §11).
    /// </summary>
    ICkArchiveRuntimeStore GetCkArchiveRuntimeStore();

    /// <summary>
    /// Returns the archive lifecycle service for this tenant, or null if stream data is not
    /// enabled (no <see cref="IStreamDataRepository"/> available). Composes the tenant-scoped
    /// dependencies (<see cref="ICkArchiveRuntimeStore"/>, <see cref="IStreamDataRepository"/>,
    /// <see cref="IArchiveAuditTrail"/>) so callers don't have to.
    /// </summary>
    IArchiveLifecycleService? GetArchiveLifecycleService();

    /// <summary>
    /// Enables stream data for this tenant: sets the configuration flag and
    /// creates the CrateDB table if needed.
    /// </summary>
    Task EnableStreamDataAsync();

    /// <summary>
    /// Disables stream data for this tenant: sets the configuration flag to disabled.
    /// Does not delete the existing data table.
    /// </summary>
    Task DisableStreamDataAsync();

    /// <summary>
    /// Returns true if stream data is enabled for this tenant.
    /// </summary>
    Task<bool> IsStreamDataEnabledAsync();

    #endregion Access Management

    #region Tenant Management

    /// <summary>
    /// Creates a new child tenant with the given database name and tenant id.
    /// </summary>
    /// <param name="adminSession">Admin session to perform the operation</param>
    /// <param name="databaseName">The database name for the new tenant</param>
    /// <param name="tenantId">The unique tenant identifier</param>
    Task CreateChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId);

    /// <summary>
    /// Creates a new child tenant with the given database name, tenant id, and optionally applies a blueprint.
    /// </summary>
    /// <param name="adminSession">Admin session to perform the operation</param>
    /// <param name="databaseName">The database name for the new tenant</param>
    /// <param name="tenantId">The unique tenant identifier</param>
    /// <param name="blueprintId">Optional blueprint to apply after creating the tenant</param>
    /// <returns>The result of the blueprint application, or null if no blueprint was specified</returns>
    Task<BlueprintApplicationResult?> CreateChildTenantAsync(IOctoAdminSession adminSession, string databaseName,
        string tenantId, BlueprintId? blueprintId);

    Task AttachChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId);

    Task DetachChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    Task ClearChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    Task DropChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    /// <summary>
    ///     Returns true if a child tenant with the given name exists.
    /// </summary>
    /// <param name="adminSession">Admin session to get the tenant context</param>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>True, if the tenant exists</returns>
    Task<bool> IsChildTenantExistingAsync(IOctoAdminSession adminSession, string tenantId);

    /// <summary>
    /// Gets all child tenants of the current tenant.
    /// </summary>
    /// <param name="adminSession">Admin session to get the tenant context</param>
    /// <param name="skip">Number of tenants to skip</param>
    /// <param name="take">Number of tenants to take</param>
    /// <returns>List of child tenants</returns>
    Task<IResultSet<OctoTenant>> GetChildTenantsAsync(IOctoAdminSession adminSession, int? skip = null,
        int? take = null);

    /// <summary>
    ///    Gets a child tenant description object with the given name
    /// </summary>
    /// <param name="adminSession">Admin session to get the tenant context</param>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>The tenant description object</returns>
    Task<OctoTenant> GetChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

    /// <summary>
    ///    Tries to get a child tenant description object with the given name
    /// </summary>
    /// <param name="adminSession">Admin session to get the tenant context</param>
    /// <param name="tenantId">The tenant id</param>
    /// <returns>The tenant description object or null if not found</returns>
    Task<OctoTenant?> TryGetChildTenantAsync(IOctoAdminSession adminSession, string tenantId);

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

    #region Database Maintenance

    /// <summary>
    ///     Creates indexes for the RtAssociations collection if they don't already exist.
    ///     This method is typically called during migrations to ensure optimal query performance.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CreateRtAssociationIndexesAsync();

    /// <summary>
    /// Updates all indexes in the tenant database.
    /// This method can be used to ensure that all indexes are up to date and optimized for
    /// the current data and query patterns.
    /// </summary>
    /// <param name="adminSession">An admin session to perform the operation</param>
    /// <returns></returns>
    Task UpdateIndexesAsync(IOctoAdminSession adminSession);

    #endregion Database Maintenance

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
