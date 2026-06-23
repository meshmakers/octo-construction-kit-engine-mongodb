using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Service for backing up and restoring tenants with lifecycle management.
/// Handles orchestration of tenant detachment/attachment and database backup/restore operations.
/// </summary>
internal class TenantBackupService(
    ISystemContext systemContext,
    IRepositoryOpsService repositoryOpsService,
    ILogger<TenantBackupService> logger) : ITenantBackupService
{
    /// <inheritdoc />
    public async Task<CommandResult> BackupTenantAsync(string tenantId, string archiveFilePath,
        bool detachTenant = false, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        try
        {
            logger.LogInformation("Starting backup for tenant '{TenantId}' to archive '{ArchiveFilePath}'",
                tenantId, archiveFilePath);

            // Verify system tenant exists
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                var errorMessage = "System tenant does not exist";
                logger.LogError(errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // Find the tenant context
            var tenantContext = await systemContext.TryFindTenantContextAsync(tenantId);
            if (tenantContext == null)
            {
                var errorMessage = $"Tenant '{tenantId}' not found";
                logger.LogError(errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // Optionally detach tenant before backup
            if (detachTenant)
            {
                logger.LogInformation("Detaching tenant '{TenantId}' before backup", tenantId);
                using var session = await systemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
                logger.LogInformation("Tenant '{TenantId}' detached successfully", tenantId);
            }

            // Perform the backup
            logger.LogInformation("Executing mongodump for database '{DatabaseName}'",
                tenantContext.DatabaseName);
            var dumpOptions = MongoDumpOptions.ForArchive(tenantContext.DatabaseName, archiveFilePath);
            var result = await repositoryOpsService.ExecuteMongoDumpAsync(dumpOptions, timeout, cancellationToken);

            if (result.Success)
            {
                logger.LogInformation(
                    "Backup completed successfully for tenant '{TenantId}' to '{ArchiveFilePath}'",
                    tenantId, archiveFilePath);
            }
            else
            {
                logger.LogError("Backup failed for tenant '{TenantId}': {Error}", tenantId, result.Error);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during backup of tenant '{TenantId}'", tenantId);
            return CommandResult.Failure($"Exception during backup: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> CloneTenantToTempAsync(string sourceTenantId, string tempTenantId,
        string tempDatabaseName, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        // The intermediate archive lands in the per-process temp dir under a unique-per-call
        // file name. Path.GetTempFileName creates an empty file as a side-effect (atomic
        // O_CREAT|O_EXCL) — we then overwrite that file with the mongodump archive.
        // Cleanup runs in finally so a failed clone doesn't leak the archive.
        var tempArchive = Path.GetTempFileName();
        try
        {
            logger.LogInformation(
                "Cloning tenant '{SourceTenantId}' to temp tenant '{TempTenantId}' (database '{TempDatabaseName}', staging archive '{Archive}')",
                sourceTenantId, tempTenantId, tempDatabaseName, tempArchive);

            // Look up the source DB name — RestoreTenantAsync uses it for the
            // mongorestore --nsFrom/--nsTo namespace mapping when target DB name differs.
            var sourceContext = await systemContext.TryFindTenantContextAsync(sourceTenantId);
            if (sourceContext == null)
            {
                return CommandResult.Failure($"Source tenant '{sourceTenantId}' not found.");
            }
            var sourceDatabaseName = sourceContext.DatabaseName;

            var backupResult = await BackupTenantAsync(sourceTenantId, tempArchive,
                detachTenant: false, timeout, cancellationToken);
            if (!backupResult.Success)
            {
                return CommandResult.Failure(
                    $"Clone failed at backup step for source tenant '{sourceTenantId}': {backupResult.Error}");
            }

            var restoreResult = await RestoreTenantAsync(tempTenantId, tempDatabaseName, tempArchive,
                sourceDatabaseName, dropExistingTenant: false, attachTenant: true, timeout, cancellationToken);
            if (!restoreResult.Success)
            {
                return CommandResult.Failure(
                    $"Clone failed at restore step (temp tenant '{tempTenantId}'): {restoreResult.Error}");
            }

            logger.LogInformation(
                "Clone of tenant '{SourceTenantId}' to '{TempTenantId}' complete",
                sourceTenantId, tempTenantId);
            return new CommandResult { Success = true, ExitCode = 0 };
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error cloning tenant '{SourceTenantId}' to '{TempTenantId}'", sourceTenantId, tempTenantId);
            return CommandResult.Failure($"Exception during clone: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchive))
                {
                    File.Delete(tempArchive);
                }
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx,
                    "Failed to delete intermediate clone archive '{Archive}' — manual cleanup may be required",
                    tempArchive);
            }
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> RestoreTenantAsync(string tenantId, string databaseName,
        string archiveFilePath, string? sourceDatabaseName = null, bool dropExistingTenant = true,
        bool attachTenant = true, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        try
        {
            logger.LogInformation(
                "Starting restore for tenant '{TenantId}' from archive '{ArchiveFilePath}' to database '{DatabaseName}'",
                tenantId, archiveFilePath, databaseName);

            // Verify system tenant exists
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                var errorMessage = "System tenant does not exist";
                logger.LogError(errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // Check if archive file exists and has content
            if (!File.Exists(archiveFilePath))
            {
                var errorMessage = $"Archive file not found: {archiveFilePath}";
                logger.LogError(errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            var archiveFileInfo = new FileInfo(archiveFilePath);
            if (archiveFileInfo.Length == 0)
            {
                var errorMessage =
                    $"Archive file is empty (0 bytes): {archiveFilePath}. The upload may not have completed successfully.";
                logger.LogError(errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // Check if tenant exists and optionally drop it
            var existingTenantContext = await systemContext.TryFindTenantContextAsync(tenantId);
            if (existingTenantContext != null)
            {
                if (dropExistingTenant)
                {
                    logger.LogInformation("Tenant '{TenantId}' already exists, dropping it before restore",
                        tenantId);
                    using var session = await systemContext.GetAdminSessionAsync();
                    session.StartTransaction();
                    await systemContext.DropChildTenantAsync(session, tenantId);
                    await session.CommitTransactionAsync();
                    logger.LogInformation("Tenant '{TenantId}' dropped successfully", tenantId);
                }
                else
                {
                    var errorMessage = $"Tenant '{tenantId}' already exists and dropExistingTenant is false";
                    logger.LogError(errorMessage);
                    return CommandResult.Failure(errorMessage);
                }
            }
            else
            {
                logger.LogInformation("Tenant '{TenantId}' does not exist, proceeding with restore", tenantId);
            }

            // Perform the restore
            logger.LogInformation("Executing mongorestore for database '{DatabaseName}'", databaseName);
            var restoreOptions = new MongoRestoreOptions
            {
                Database = databaseName,
                Archive = archiveFilePath,
                Gzip = true,
                Drop = true
            };

            // Enable namespace mapping if restoring to a different database name
            if (!string.IsNullOrEmpty(sourceDatabaseName) && sourceDatabaseName != databaseName)
            {
                restoreOptions.NsFrom = $"{sourceDatabaseName}.*";
                restoreOptions.NsTo = $"{databaseName}.*";
                logger.LogInformation(
                    "Using namespace mapping: '{NsFrom}' -> '{NsTo}'",
                    restoreOptions.NsFrom, restoreOptions.NsTo);
            }

            var result = await repositoryOpsService.ExecuteMongoRestoreAsync(restoreOptions, timeout,
                cancellationToken);

            if (!result.Success)
            {
                logger.LogError("Restore failed for tenant '{TenantId}': {Error}", tenantId, result.Error);
                return result;
            }

            logger.LogInformation("Database '{DatabaseName}' restored successfully for tenant '{TenantId}'",
                databaseName, tenantId);

            // Optionally attach tenant after restore
            if (attachTenant)
            {
                logger.LogInformation("Attaching tenant '{TenantId}' to database '{DatabaseName}'",
                    tenantId, databaseName);
                using var session = await systemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, databaseName, tenantId);
                await session.CommitTransactionAsync();
                logger.LogInformation(
                    "Tenant '{TenantId}' successfully restored and attached to database '{DatabaseName}'",
                    tenantId, databaseName);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during restore of tenant '{TenantId}'", tenantId);
            return CommandResult.Failure($"Exception during restore: {ex.Message}");
        }
    }
}
