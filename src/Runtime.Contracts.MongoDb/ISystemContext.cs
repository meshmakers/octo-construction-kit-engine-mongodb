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

    /// <summary>
    /// Ensures that the system construction kit model is imported into the tenant with the correct version.
    /// </summary>
    /// <returns>></returns>
    Task EnsureSystemCkModelAsync();

    /// <summary>
    /// Invalidates the per-process tenant-resolve auto-import guards for the given tenant so the next
    /// resolve re-imports its service-managed CK models (e.g. <c>System.UI</c>) and StreamData model.
    /// </summary>
    /// <remarks>
    /// Call this from the Pre-update / Pre-delete tenant lifecycle events. A delete+recreate of a tenant
    /// within one process lifetime would otherwise skip the guarded auto-import and leave the fresh tenant
    /// without those models (AB#4294 regression). Safe no-op when the tenant has no guard entries.
    /// </remarks>
    /// <param name="tenantId">The tenant whose resolve-import guards should be cleared.</param>
    void InvalidateTenantResolveImportGuards(string tenantId);

    #region Backup and Restore

    /// <summary>
    /// Creates a backup of a tenant's database to a gzipped archive file.
    /// </summary>
    /// <param name="tenantId">The tenant ID to backup</param>
    /// <param name="archiveFilePath">Path where the backup archive will be created</param>
    /// <param name="detachTenant">If true, detaches the tenant before backup (default: false)</param>
    /// <param name="timeout">Timeout for dump operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Command result indicating success or failure of the backup operation</returns>
    Task<CommandResult> BackupTenantAsync(string tenantId, string archiveFilePath,
        bool detachTenant = false, TimeSpan? timeout = null, CancellationToken? cancellationToken = null);

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

    /// <summary>
    /// Clones an existing tenant into a new temporary tenant via dump+restore. The caller
    /// is responsible for calling <see cref="ITenantContext.DropChildTenantAsync"/> on the
    /// temp tenant when done — this method does not auto-cleanup the temp tenant.
    /// </summary>
    /// <remarks>
    /// AB#4209 Step 5 — used by the <c>DumpTenant --clean</c> orchestrator
    /// (bot-services) to clone a tenant into an isolated temp tenant before stripping
    /// <c>overlay:*</c> URI entries and re-dumping. Cloning isolates the cleanup from the
    /// live tenant so OIDC traffic against the source is unaffected.
    /// </remarks>
    /// <param name="sourceTenantId">The tenant to clone from. Must be attached.</param>
    /// <param name="tempTenantId">The new tenant ID to attach the clone as. Must not already exist.</param>
    /// <param name="tempDatabaseName">The new database name to restore the clone into. Must not already exist.</param>
    /// <param name="timeout">Timeout applied to each of the underlying dump + restore steps independently.</param>
    /// <param name="cancellationToken">Cancellation token propagated to both steps.</param>
    Task<CommandResult> CloneTenantToTempAsync(string sourceTenantId, string tempTenantId,
        string tempDatabaseName, TimeSpan? timeout = null, CancellationToken? cancellationToken = null);

    /// <summary>
    /// Checks whether a physical database with the given name exists.
    /// </summary>
    /// <param name="databaseName">The database name to check (used verbatim, no normalization)</param>
    Task<bool> IsDatabaseExistingAsync(string databaseName);

    #endregion
}
