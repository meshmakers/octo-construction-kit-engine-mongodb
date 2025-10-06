namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
/// Service for backing up and restoring tenants with lifecycle management.
/// Handles orchestration of tenant detachment/attachment and database backup/restore operations.
/// </summary>
internal interface ITenantBackupService
{
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
    /// <param name="dropExistingTenant">If true, drops existing tenant before restore (default: true)</param>
    /// <param name="attachTenant">If true, attaches the tenant after restore (default: true)</param>
    /// <param name="timeout">Timeout for restore operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Command result indicating success or failure of the restore operation</returns>
    Task<CommandResult> RestoreTenantAsync(string tenantId, string databaseName, string archiveFilePath,
        bool dropExistingTenant = true, bool attachTenant = true,
        TimeSpan? timeout = null, CancellationToken? cancellationToken = null);
}
