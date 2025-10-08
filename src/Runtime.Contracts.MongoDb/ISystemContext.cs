using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

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
    /// Gets based on the tenant id the tenant context.
    /// </summary>
    /// <param name="tenantId">The tenant id (also supports the system tenant id)</param>
    /// <returns>The tenant context or null if not found</returns>
    Task<ITenantContext?> TryFindTenantContextAsync(string tenantId);

    /// <summary>
    /// Gets based on the tenant id the tenant repository.
    /// </summary>
    /// <param name="tenantId">The tenant id (also supports the system tenant id)</param>
    /// <returns>The tenant repository</returns>
    Task<ITenantRepository> FindTenantRepositoryAsync(string tenantId);

    /// <summary>
    /// Tries to get based on the tenant id the tenant repository.
    /// </summary>
    /// <param name="tenantId">The tenant id (also supports the system tenant id)</param>
    /// <returns>The tenant repository or null if not found</returns>
    Task<ITenantRepository?> TryFindTenantRepositoryAsync(string tenantId);

    #region Backup and Restore

    /// <summary>
    /// Creates a backup of a tenant's database to a gzipped archive file.
    /// </summary>
    /// <param name="tenantId">The tenant ID to backup</param>
    /// <param name="archiveFilePath">Path where the backup archive will be created</param>
    /// <param name="detachTenant">If true, detaches the tenant before backup (default: false)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Command result indicating success or failure of the backup operation</returns>
    Task<CommandResult> BackupTenantAsync(string tenantId, string archiveFilePath,
        bool detachTenant = false, CancellationToken? cancellationToken = null);

    /// <summary>
    /// Restores a tenant's database from a gzipped archive file.
    /// </summary>
    /// <param name="tenantId">The tenant ID to restore</param>
    /// <param name="databaseName">The database name to restore to</param>
    /// <param name="archiveFilePath">Path to the backup archive file</param>
    /// <param name="sourceDatabaseName">Optional source database name in the archive. If provided and different from databaseName, enables namespace mapping to restore to a different database name.</param>
    /// <param name="dropExistingTenant">If true, drops existing tenant before restore (default: true)</param>
    /// <param name="attachTenant">If true, attaches the tenant after restore (default: true)</param>
    /// <param name="timeout">Timeout for restore operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Command result indicating success or failure of the restore operation</returns>
    Task<CommandResult> RestoreTenantAsync(string tenantId, string databaseName, string archiveFilePath,
        string? sourceDatabaseName = null, bool dropExistingTenant = true, bool attachTenant = true,
        TimeSpan? timeout = null, CancellationToken? cancellationToken = null);

    #endregion
}
