using System.Runtime.CompilerServices;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Service for backing up and restoring tenants with lifecycle management.
/// Handles orchestration of tenant detachment/attachment and database backup/restore operations.
/// </summary>
internal class TenantBackupService(
    ISystemContext systemContext,
    IRepositoryOpsService repositoryOpsService,
    ILogger<TenantBackupService> logger,
    TenantOwnershipStore tenantOwnershipStore,
    TenantDatabaseRegistrationLookup registrationLookup,
    SystemDatabaseMarkerStore systemDatabaseMarkerStore,
    RestoredPlatformOnlyValueRemover platformOnlyValueRemover,
    ITenantLifecycleStore? tenantLifecycleStore = null,
    IOptions<OctoSystemConfiguration>? systemConfiguration = null) : ITenantBackupService
{
    /// <inheritdoc />
    public async Task<CommandResult> BackupTenantAsync(string tenantId, string archiveFilePath,
        bool detachTenant = false, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        // Outside the try: a dump that throws after a committed detach whose notification failed must still name the
        // recovery in its failure result.
        string? deleteNotificationWarning = null;
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
                // The tenant-deleted notification is published here, after the commit: the services tear the tenant
                // down when they receive it, so it must not go out while the detach can still roll back.
                using var session = await systemContext.GetAdminSessionAsync();
                using var notification = TenantNotificationScope.Begin(session);
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
                logger.LogInformation("Tenant '{TenantId}' detached successfully", tenantId);
                deleteNotificationWarning = await PublishTenantDeletedAsync(notification, tenantId, "detached");
            }

            // Perform the backup
            logger.LogInformation("Executing mongodump for database '{DatabaseName}'",
                tenantContext.DatabaseName);
            var dumpOptions = MongoDumpOptions.ForTenantArchive(tenantContext.DatabaseName, archiveFilePath);
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

            AppendWarning(result, deleteNotificationWarning);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during backup of tenant '{TenantId}'", tenantId);
            var failure = CommandResult.Failure($"Exception during backup: {ex.Message}");
            AppendWarning(failure, deleteNotificationWarning);
            return failure;
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
            return new CommandResult
            {
                Success = true, ExitCode = 0,
                TenantCreatedNotificationFailed = restoreResult.TenantCreatedNotificationFailed,
                Output = restoreResult.TenantCreatedNotificationFailed ? restoreResult.Output : string.Empty,
                RemovedPlatformOnlyValues = restoreResult.RemovedPlatformOnlyValues
            };
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
        bool attachTenant = true, TimeSpan? timeout = null, CancellationToken? cancellationToken = null,
        bool overrideForeignOwnership = false)
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

            // The restore runs mongorestore --drop against the operator-supplied database name. The
            // registry check further down cannot protect the system database (the system tenant has
            // no RtTenant self-record — the same blind spot the namespace gate covers via the
            // configuration, AB#4762) nor MongoDB's own databases, so both are refused explicitly
            // here — and BEFORE the dropExistingTenant block, which is itself destructive.
            var normalizedTargetDatabaseName = databaseName.Trim();
            if (string.Equals(normalizedTargetDatabaseName, systemContext.DatabaseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(RefuseRestore(tenantId, normalizedTargetDatabaseName,
                    "it is the system database of this instance; the restore would overwrite the entire platform"));
            }

            if (string.Equals(normalizedTargetDatabaseName, "admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedTargetDatabaseName, "local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedTargetDatabaseName, "config", StringComparison.OrdinalIgnoreCase))
            {
                var errorMessage =
                    $"Database '{databaseName}' is reserved by MongoDB itself and can never be a tenant " +
                    $"database. Refusing to restore tenant '{tenantId}' into it.";
                logger.LogError("Restore failed for tenant '{TenantId}': {ErrorMessage}", tenantId, errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // AB#8672: the checks above and the registry check further down only know THIS instance. On a
            // shared MongoDB server the target can belong to another OctoMesh instance (different
            // SystemDatabaseName, Epic AB#4944), and mongorestore --drop would destroy it before the attach
            // guard (AB#4945) refuses the result. Runs before the dropExistingTenant block, which is itself
            // destructive.
            var (foreignOwnershipRefusal, approvedForeignMarker) = await CheckForeignOwnershipAsync(tenantId,
                normalizedTargetDatabaseName, overrideForeignOwnership, attachTenant,
                cancellationToken ?? CancellationToken.None);
            if (foreignOwnershipRefusal != null)
            {
                return CommandResult.Failure(foreignOwnershipRefusal);
            }

            var (prefixRefusal, prefixExemptName) = await CheckTenantDatabasePrefixAsync(tenantId, normalizedTargetDatabaseName);
            using var prefixExemption = prefixExemptName != null ? TenantDatabasePrefixExemption.Begin(prefixExemptName) : null;
            if (prefixRefusal != null)
            {
                return CommandResult.Failure(prefixRefusal);
            }

            // AB#4829: the restore mongorestores into an UNREGISTERED database first and attaches
            // only afterwards — so a restore that targets the tenant id or database name of a tenant
            // deleted moments ago races the delete settle sweep, which sees "registry absent +
            // database exists + no owner" and would drop the restore mid-flight. Refuse while the
            // Deleting tombstone stands; the sweep clears it within roughly two minutes.
            if (tenantLifecycleStore is not null)
            {
                var normalizedTenantId = tenantId.NormalizeString();
                var lifecycleRecords = await tenantLifecycleStore
                    .ListAsync(cancellationToken ?? CancellationToken.None);
                var settling = lifecycleRecords.FirstOrDefault(r =>
                    r.State == TenantLifecycleState.Deleting
                    && (r.TenantId == normalizedTenantId
                        || string.Equals(r.DatabaseName, normalizedTargetDatabaseName,
                            StringComparison.OrdinalIgnoreCase)));
                if (settling is not null)
                {
                    var errorMessage =
                        $"A deletion involving tenant '{settling.TenantId}' (database " +
                        $"'{settling.DatabaseName}') is still settling. Refusing to restore into its " +
                        "namespace until the settle sweep has completed the delete; retry in a couple " +
                        "of minutes.";
                    logger.LogError("Restore failed for tenant '{TenantId}': {ErrorMessage}", tenantId,
                        errorMessage);
                    return CommandResult.Failure(errorMessage);
                }
            }

            // AB#4367: determine the archive's source database(s) from its prelude so a restore
            // under a different database name gets the namespace mapping instead of silently
            // restoring nothing. Runs before the drop block so a doomed restore never destroys
            // an existing target tenant.
            var detectedDatabases = MongoArchivePreludeReader.TryReadSourceDatabases(archiveFilePath);
            var effectiveSourceDatabaseName = sourceDatabaseName;
            if (!string.IsNullOrEmpty(sourceDatabaseName))
            {
                if (detectedDatabases.Count > 0 && !detectedDatabases.Contains(sourceDatabaseName))
                {
                    var errorMessage =
                        $"Archive contains database(s) '{string.Join("', '", detectedDatabases)}' but source " +
                        $"database '{sourceDatabaseName}' was specified — nothing would be restored. " +
                        "Pass the database name the archive was dumped from.";
                    logger.LogError(
                        "Restore of tenant '{TenantId}' rejected: {ErrorMessage}", tenantId, errorMessage);
                    return CommandResult.Failure(errorMessage);
                }
            }
            else if (detectedDatabases.Count == 1)
            {
                effectiveSourceDatabaseName = detectedDatabases[0];
                logger.LogInformation(
                    "Auto-detected source database '{SourceDatabaseName}' from archive prelude",
                    effectiveSourceDatabaseName);
            }
            else if (detectedDatabases.Count > 1 && !detectedDatabases.Contains(databaseName))
            {
                var errorMessage =
                    $"Archive contains multiple databases ('{string.Join("', '", detectedDatabases)}'). " +
                    "Specify the source database name to restore.";
                logger.LogError("Restore of tenant '{TenantId}' rejected: {ErrorMessage}", tenantId, errorMessage);
                return CommandResult.Failure(errorMessage);
            }
            else if (detectedDatabases.Count == 0)
            {
                logger.LogWarning(
                    "Could not detect the source database from archive '{ArchiveFilePath}' — assuming it matches the target database '{DatabaseName}'",
                    archiveFilePath, databaseName);
            }

            // The restore below runs with Drop = true against an operator-supplied database name, so it
            // silently overwrites whatever lives there. Everything else validating that name happens
            // AFTER the data is already gone (the attach at the end), which made this a second route to
            // the AB#4762 data loss. Refuse up front when the name belongs to a different tenant - before the
            // existing tenant is dropped, so a refused restore destroys nothing.
            // (The system database and MongoDB's own databases are refused earlier — they carry no
            // RtTenant registry record, so this lookup is blind to them.)
            var claimingTenantId = await systemContext.TryGetTenantIdByDatabaseNameAsync(databaseName);
            if (claimingTenantId != null && !string.Equals(claimingTenantId, tenantId.NormalizeString(),
                    StringComparison.Ordinal))
            {
                var errorMessage =
                    $"Database '{databaseName}' is registered to tenant '{claimingTenantId}'. Refusing to restore " +
                    $"tenant '{tenantId}' into it, because the restore would overwrite that tenant's data.";
                logger.LogError("Restore failed for tenant '{TenantId}': {ErrorMessage}", tenantId, errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // AB#8672: the ownership check above only reads. Before anything is dropped or written, the restore
            // CLAIMS the target for this instance (insert-if-absent marker with a claim token) and holds the claim
            // through the whole restore, so another instance can neither attach nor restore the database in
            // between. mongorestore leaves the claim alone (the marker collections are excluded from the restore);
            // the attach at the end takes it over, and a restore that ends without attach or fails releases it.
            var claim = new RestoreClaim(tenantOwnershipStore, databaseName, tenantId.NormalizeString(),
                systemContext.DatabaseName.Trim().ToLowerInvariant(),
                await systemContext.IsDatabaseExistingAsync(databaseName));
            var claimRefusal = await claim.AcquireAsync(approvedForeignMarker,
                cancellationToken ?? CancellationToken.None);
            if (claimRefusal != null)
            {
                return CommandResult.Failure(RefuseRestore(tenantId, databaseName, claimRefusal));
            }

            var restored = false;
            try
            {
                var result = await RestoreClaimedTargetAsync(tenantId, databaseName, archiveFilePath,
                    effectiveSourceDatabaseName, detectedDatabases, dropExistingTenant, attachTenant,
                    overrideForeignOwnership, claim, timeout, cancellationToken);
                restored = result.Success;
                return result;
            }
            finally
            {
                if (claim.IsKept)
                {
                    logger.LogError(
                        "Restore of tenant '{TenantId}': the claim on database '{DatabaseName}' is kept, so no other instance attaches it; make sure mongorestore has stopped, then drop the database by hand",
                        tenantId, databaseName);
                }
                else if (!restored || !attachTenant)
                {
                    // Failed: release the claim (and the database the claim materialized, when nothing else got into
                    // it). Restored without attach: the database stays unowned like any detached database.
                    await claim.ReleaseAsync(dropIfOnlyTheClaim: !restored);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during restore of tenant '{TenantId}'", tenantId);
            return CommandResult.Failure($"Exception during restore: {ex.Message}");
        }
    }

    /// <summary>
    ///     The destructive part of the restore, under the claim of <paramref name="claim" /> (AB#8672): drop the existing
    ///     tenant if asked, mongorestore, attach. A tenant-deleted notification of the drop that could not be published
    ///     is named in the result's <see cref="CommandResult.Output" />, whatever the result: also when a later step
    ///     throws, the failure result carries it (the same failure the caller's catch would return, plus the warning).
    /// </summary>
    private async Task<CommandResult> RestoreClaimedTargetAsync(string tenantId, string databaseName,
        string archivePath, string? effectiveSourceDatabaseName, IReadOnlyList<string> detectedDatabases,
        bool dropExistingTenant, bool attachTenant, bool overrideForeignOwnership, RestoreClaim claim,
        TimeSpan? timeout, CancellationToken? cancellationToken)
    {
        var deleteNotificationWarning = new StrongBox<string?>();
        CommandResult result;
        try
        {
            result = await RestoreClaimedTargetCoreAsync(tenantId, databaseName, archivePath,
                effectiveSourceDatabaseName, detectedDatabases, dropExistingTenant, attachTenant,
                overrideForeignOwnership, claim, deleteNotificationWarning, timeout, cancellationToken);
        }
        catch (Exception ex) when (deleteNotificationWarning.Value != null)
        {
            logger.LogError(ex, "Error during restore of tenant '{TenantId}'", tenantId);
            result = CommandResult.Failure($"Exception during restore: {ex.Message}");
        }

        AppendWarning(result, deleteNotificationWarning.Value);
        return result;
    }

    private async Task<CommandResult> RestoreClaimedTargetCoreAsync(string tenantId, string databaseName,
        string archivePath, string? effectiveSourceDatabaseName, IReadOnlyList<string> detectedDatabases,
        bool dropExistingTenant, bool attachTenant, bool overrideForeignOwnership, RestoreClaim claim,
        StrongBox<string?> deleteNotificationWarning, TimeSpan? timeout, CancellationToken? cancellationToken)
    {
        // Check if tenant exists and optionally drop it
        var existingTenantContext = await systemContext.TryFindTenantContextAsync(tenantId);
        if (existingTenantContext != null)
        {
            if (!dropExistingTenant)
            {
                var errorMessage = $"Tenant '{tenantId}' already exists and dropExistingTenant is false";
                logger.LogError(errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            logger.LogInformation("Tenant '{TenantId}' already exists, dropping it before restore", tenantId);
            // The delete has its own ownership guard (AB#8807) for the existing tenant's database. The
            // administrator's override was checked against the restore TARGET only (AB#8672), so it
            // covers the drop only when the existing tenant lives in that very database; a different
            // database of the existing tenant is dropped only when the delete guard lets it go on its own.
            var existingDatabaseIsTarget = string.Equals(
                TenantDatabaseRegistrationLookup.Normalize(existingTenantContext.DatabaseName),
                TenantDatabaseRegistrationLookup.Normalize(databaseName),
                StringComparison.Ordinal);
            using (var session = await systemContext.GetAdminSessionAsync())
            using (var dropNotification = TenantNotificationScope.Begin(session))
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId,
                    overrideForeignOwnership: overrideForeignOwnership && existingDatabaseIsTarget);
                await session.CommitTransactionAsync();
                logger.LogInformation("Tenant '{TenantId}' dropped successfully", tenantId);

                // After the commit (see the detach of the backup); a failure does not stop the restore.
                deleteNotificationWarning.Value =
                    await PublishTenantDeletedAsync(dropNotification, tenantId, "dropped");
            }

            if (existingDatabaseIsTarget)
            {
                // The claim went with the dropped database: claim the target again before mongorestore writes into
                // it. Until then the database does not exist, so no attach can take it.
                claim.TargetDropped();
                var reclaimRefusal = await claim.AcquireAsync(approvedForeignMarker: null,
                    cancellationToken ?? CancellationToken.None);
                if (reclaimRefusal != null)
                {
                    return CommandResult.Failure(RefuseRestore(tenantId, databaseName, reclaimRefusal));
                }
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
            Archive = archivePath,
            Gzip = true,
            Drop = true,
            // AB#8672: the archive carries the ownership markers of the instance it was dumped from. They are never
            // restored: the claim of this restore stays in place, and a restored copy never looks like a database
            // of the source instance (its tenant marker or its system-database marker).
            NsExclude =
            [
                $"*.{TenantOwnershipStore.CollectionName}", $"*.{SystemDatabaseMarkerStore.CollectionName}",
                .. PlatformOnlyRestoreValues.LeftOutCollections.Select(collectionName => $"*.{collectionName}")
            ]
        };

        // Enable namespace mapping if restoring to a different database name
        if (!string.IsNullOrEmpty(effectiveSourceDatabaseName) && effectiveSourceDatabaseName != databaseName)
        {
            restoreOptions.NsFrom = $"{effectiveSourceDatabaseName}.*";
            restoreOptions.NsTo = $"{databaseName}.*";
            logger.LogInformation(
                "Using namespace mapping: '{NsFrom}' -> '{NsTo}'",
                restoreOptions.NsFrom, restoreOptions.NsTo);
        }

        CommandResult result;
        try
        {
            result = await repositoryOpsService.ExecuteMongoRestoreAsync(restoreOptions, timeout,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "mongorestore of tenant '{TenantId}' into database '{DatabaseName}' threw; it may still write, the database is dropped",
                tenantId, databaseName);
            result = CommandResult.Failure($"Exception during restore: {exception.Message}");
            result.ProcessMayStillRun = true;
        }

        if (!result.Success)
        {
            logger.LogError("Restore failed for tenant '{TenantId}': {Error}", tenantId, result.Error);
            if (result.Cancelled || result.ProcessMayStillRun)
            {
                await DropInterruptedRestoreTargetAsync(tenantId, databaseName, result, claim);
                return result;
            }

            if (result.ProcessNeverStarted)
            {
                return result;
            }

            await RemovePlatformOnlyValuesAfterFailedRestoreAsync(tenantId, databaseName, claim);
            return result;
        }

        // Fail closed: mongorestore wrote, so every exit before the platform-only values are removed drops the target.
        IReadOnlyList<string> removedPlatformOnlyValues;
        try
        {
            // AB#4367: a mongorestore whose --nsInclude matched no namespace exits 0 with
            // "0 documents restored" and writes nothing - the target then holds no more than the claim of this
            // restore. Turn that silent no-op into a descriptive failure instead of a misleading downstream attach error.
            if (await tenantOwnershipStore.HoldsOnlyMarkersAsync(databaseName,
                    cancellationToken ?? CancellationToken.None))
            {
                var archiveInfo = detectedDatabases.Count > 0
                    ? $" The archive contains database(s) '{string.Join("', '", detectedDatabases)}'."
                    : string.Empty;
                var errorMessage =
                    $"mongorestore completed but restored no data into database '{databaseName}' — " +
                    $"no namespaces in the archive matched.{archiveInfo}";
                logger.LogError("Restore failed for tenant '{TenantId}': {ErrorMessage}", tenantId, errorMessage);
                return CommandResult.Failure(errorMessage);
            }

            // A restored copy of a dumped system database is a tenant database and must not look like another
            // instance's system database. The marker collection is excluded from the restore above; this removes one
            // an older mongorestore without namespace exclusion may still have written.
            await systemDatabaseMarkerStore.RemoveAsync(databaseName, cancellationToken ?? CancellationToken.None);

            // The values only the platform may hold never reach the tenant, also from an older or edited archive.
            // Removed before the attach, so no service reads them in between.
            removedPlatformOnlyValues = await platformOnlyValueRemover.RemoveAsync(databaseName,
                cancellationToken ?? CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Restore of tenant '{TenantId}' failed before the platform-only values were removed; the restored database '{DatabaseName}' is dropped",
                tenantId, databaseName);
            await DropRestoredTargetAsync(tenantId, databaseName, claim);
            throw;
        }

        result.RemovedPlatformOnlyValues = removedPlatformOnlyValues;
        if (removedPlatformOnlyValues.Count > 0)
        {
            logger.LogWarning(
                "Restore of tenant '{TenantId}' left out {Count} platform-only value(s): {Paths}",
                tenantId, removedPlatformOnlyValues.Count, string.Join(", ", removedPlatformOnlyValues));
        }

        logger.LogInformation("Database '{DatabaseName}' restored successfully for tenant '{TenantId}'",
            databaseName, tenantId);

        // Optionally attach tenant after restore. The attach finds this restore's claim (a marker of this instance)
        // and takes it over in its transaction.
        if (attachTenant)
        {
            logger.LogInformation("Attaching tenant '{TenantId}' to database '{DatabaseName}'",
                tenantId, databaseName);
            // The tenant-created notification is published here, after the commit: the services set the tenant up
            // when they receive it and must find it registered. A failed publish does not fail the restore (the data is
            // restored and attached); it is reported on the result, so the caller can surface it.
            using var session = await systemContext.GetAdminSessionAsync();
            using var notification = TenantNotificationScope.Begin(session);
            session.StartTransaction();
            await systemContext.AttachChildTenantAsync(session, databaseName, tenantId);
            await session.CommitTransactionAsync();

            logger.LogInformation(
                "Tenant '{TenantId}' successfully restored and attached to database '{DatabaseName}'",
                tenantId, databaseName);

            try
            {
                await notification.PublishAsync();
            }
            catch (Exception ex)
            {
                var warning =
                    $"Tenant '{tenantId}' was restored and attached to database '{databaseName}', but the " +
                    $"tenant-created notification failed ({ex.Message}). The services have not set the tenant up: " +
                    "run ClearCache for the tenant.";
                logger.LogWarning(ex, "Restore of tenant '{TenantId}': {Warning}", tenantId, warning);
                result.TenantCreatedNotificationFailed = true;
                result.Output = string.IsNullOrEmpty(result.Output) ? warning : result.Output + Environment.NewLine + warning;
            }
        }

        return result;
    }

    /// <summary>
    ///     Publishes the tenant-deleted notification collected by <paramref name="notification" /> after the commit of a
    ///     drop or detach this service ran itself. A failure does not fail the operation (the removal is committed); it
    ///     is logged and returned as the warning for the result.
    /// </summary>
    /// <returns><c>null</c> when it was published, else the warning naming the tenant and the recovery.</returns>
    private async Task<string?> PublishTenantDeletedAsync(TenantNotificationScope notification, string tenantId,
        string removal)
    {
        try
        {
            await notification.PublishAsync();
            return null;
        }
        catch (Exception ex)
        {
            var warning =
                $"Tenant '{tenantId}' was {removal}, but the tenant-deleted notification failed ({ex.Message}). " +
                "The services may still hold the tenant's cached state: run ClearCache for the tenant.";
            logger.LogWarning(ex, "Tenant '{TenantId}': {Warning}", tenantId, warning);
            return warning;
        }
    }

    private static void AppendWarning(CommandResult result, string? warning)
    {
        if (warning == null)
        {
            return;
        }

        result.Output = string.IsNullOrEmpty(result.Output) ? warning : result.Output + Environment.NewLine + warning;
    }

    /// <summary>
    ///     A cancelled mongorestore, or one that may still run, leaves a database whose content nobody can vouch for: it is
    ///     dropped, never cleaned while it may still be written. While mongorestore may still run, the dropped target is
    ///     claimed again for this instance and the claim is kept, so no other instance attaches what it writes.
    /// </summary>
    private async Task DropInterruptedRestoreTargetAsync(string tenantId, string databaseName, CommandResult result,
        RestoreClaim claim)
    {
        logger.LogError(
            "Restore of tenant '{TenantId}' was interrupted (cancelled: {Cancelled}, process may still run: {ProcessMayStillRun}); the target database '{DatabaseName}' is dropped",
            tenantId, result.Cancelled, result.ProcessMayStillRun, databaseName);
        var dropped = await DropRestoredTargetAsync(tenantId, databaseName, claim);
        var dropNote = dropped
            ? $"The target database '{databaseName}' was dropped."
            : $"The target database '{databaseName}' could not be dropped; the claim of this instance on it is kept. Drop it by hand.";
        if (dropped && result.ProcessMayStillRun)
        {
            var claimKept = await KeepClaimWhileMongoRestoreMayRunAsync(tenantId, databaseName, claim);
            dropNote = claimKept
                ? $"The target database '{databaseName}' was dropped, but mongorestore may still be running: the database is claimed again for this instance and the claim is kept, so no other instance attaches it. Make sure mongorestore has stopped, then drop the database by hand."
                : $"The target database '{databaseName}' was dropped, but mongorestore may still be running and the database could not be claimed again: make sure mongorestore has stopped and drop the database by hand if it reappears.";
        }

        result.Error = string.IsNullOrEmpty(result.Error) ? dropNote : result.Error + Environment.NewLine + dropNote;
    }

    private async Task<bool> KeepClaimWhileMongoRestoreMayRunAsync(string tenantId, string databaseName,
        RestoreClaim claim)
    {
        try
        {
            claim.TargetDropped();
            var reclaimRefusal = await claim.AcquireAsync(approvedForeignMarker: null, CancellationToken.None);
            if (reclaimRefusal != null)
            {
                logger.LogError(
                    "Restore of tenant '{TenantId}': the dropped database '{DatabaseName}' could not be claimed again while mongorestore may still run: {Refusal}",
                    tenantId, databaseName, reclaimRefusal);
                return false;
            }

            claim.Keep();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Restore of tenant '{TenantId}': the dropped database '{DatabaseName}' could not be claimed again while mongorestore may still run",
                tenantId, databaseName);
            return false;
        }
    }

    /// <summary>Drops the restored target; when the drop fails, the claim is kept so no other instance attaches it.</summary>
    private async Task<bool> DropRestoredTargetAsync(string tenantId, string databaseName, RestoreClaim claim)
    {
        var dropped = await platformOnlyValueRemover.DropRestoredDatabaseAsync(databaseName);
        if (!dropped)
        {
            logger.LogError(
                "Restore of tenant '{TenantId}': the restored database '{DatabaseName}' could not be dropped; the claim on it is kept",
                tenantId, databaseName);
            claim.Keep();
        }

        return dropped;
    }

    /// <summary>
    ///     A mongorestore that failed or timed out may have written documents, and a database left behind can be attached
    ///     later: the platform-only values are removed from it, and when that fails too it is dropped.
    /// </summary>
    private async Task RemovePlatformOnlyValuesAfterFailedRestoreAsync(string tenantId, string databaseName,
        RestoreClaim claim)
    {
        try
        {
            var removedPlatformOnlyValues =
                await platformOnlyValueRemover.RemoveAsync(databaseName, CancellationToken.None);
            if (removedPlatformOnlyValues.Count > 0)
            {
                logger.LogWarning(
                    "Failed restore of tenant '{TenantId}': removed {Count} platform-only value(s) from the partly restored database '{DatabaseName}'",
                    tenantId, removedPlatformOnlyValues.Count, databaseName);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Failed restore of tenant '{TenantId}': the platform-only values could not be removed from the partly restored database '{DatabaseName}'; it is dropped",
                tenantId, databaseName);
            await DropRestoredTargetAsync(tenantId, databaseName, claim);
        }
    }

    /// <summary>
    ///     The ownership claim a restore holds on its target from before the first destructive step until the attach
    ///     (AB#8672). Only a claim THIS restore inserted is ever released; a marker of this instance that was there
    ///     before (a restore over the instance's own tenant) is left alone.
    /// </summary>
    internal sealed class RestoreClaim(TenantOwnershipStore store, string databaseName, string tenantId,
        string ownerSystemDatabaseName, bool databaseExistedBefore)
    {
        private readonly DateTime _claimTime = TenantOwnershipStore.NewClaimTime();
        private bool _inserted;
        private bool _materializedByClaim = !databaseExistedBefore;

        /// <summary>The claim stays on the target when the restore ends: its content may not be attached elsewhere.</summary>
        public bool IsKept { get; private set; }

        /// <summary>The restore dropped the target (the existing tenant's database): a new claim materializes it again.</summary>
        public void TargetDropped() => _materializedByClaim = true;

        /// <summary>Keeps the claim when the restore ends; <see cref="ReleaseAsync" /> then leaves it in place.</summary>
        public void Keep() => IsKept = true;

        /// <summary>
        ///     Claims the target; returns the logged refusal reason when another instance holds it.
        ///     <paramref name="approvedForeignMarker" /> is the foreign marker the ownership guard granted the
        ///     deliberate override for (null: no override); only exactly that marker is taken over (AB#8672).
        /// </summary>
        public async Task<string?> AcquireAsync(TenantOwnershipMarkerIdentity? approvedForeignMarker,
            CancellationToken cancellationToken)
        {
            var marker = await store.StampIfAbsentAndGetAsync(databaseName, tenantId, ownerSystemDatabaseName,
                cancellationToken, _claimTime);
            if (string.Equals(marker.OwnerSystemDatabaseName, ownerSystemDatabaseName, StringComparison.Ordinal))
            {
                _inserted |= TenantOwnershipStore.IsClaim(marker, ownerSystemDatabaseName, tenantId, _claimTime);
                return null;
            }

            // The ownership guard granted the deliberate override for one foreign marker: take over exactly that
            // marker and only while it is still on the database. Another instance may have attached or re-created the
            // database since the check - its marker carries another stamp - and its data must not be replaced.
            if (approvedForeignMarker != null &&
                string.Equals(marker.OwnerSystemDatabaseName, approvedForeignMarker.OwnerSystemDatabaseName,
                    StringComparison.Ordinal) &&
                marker.AttachedAtUtc == approvedForeignMarker.StampedAtUtc &&
                await store.TryTakeOverMarkerAsync(databaseName, marker, tenantId, ownerSystemDatabaseName,
                    _claimTime, cancellationToken))
            {
                _inserted = true;
                return null;
            }

            return $"another OctoMesh instance (owner system database '{marker.OwnerSystemDatabaseName}', tenant " +
                   $"'{marker.TenantId}') claimed the database concurrently, after the ownership check of this " +
                   "restore; nothing was dropped or written by the restore";
        }

        /// <summary>
        ///     Releases a claim this restore inserted. With <paramref name="dropIfOnlyTheClaim" />, a database the claim
        ///     materialized (a new name, or the existing tenant's database this restore dropped) and nothing else got
        ///     into is dropped again, so a failed restore leaves no empty database behind that would block the name.
        /// </summary>
        public async Task ReleaseAsync(bool dropIfOnlyTheClaim)
        {
            if (!_inserted || IsKept)
            {
                return;
            }

            if (dropIfOnlyTheClaim && _materializedByClaim &&
                await store.TryDropIfOnlyClaimAsync(databaseName, ownerSystemDatabaseName, tenantId, _claimTime))
            {
                return;
            }

            await store.TryRemoveClaimAsync(databaseName, ownerSystemDatabaseName, tenantId, _claimTime);
        }
    }

    /// <summary>
    ///     Cross-instance ownership guard of the restore (AB#8672). Returns the refusal message when the
    ///     target database belongs to another OctoMesh instance on the same MongoDB server, or null when the
    ///     restore may proceed. The refusal message is the uniform conflict of attach and delete
    ///     (<see cref="TenantException.DatabaseNameNotAvailable" />); the reason with the owner is logged only
    ///     (<see cref="RefuseRestore" />). A database belongs to another instance when it is that instance's system
    ///     database, when its ownership marker (AB#4945) names another instance, or when another instance's
    ///     system database registers it (a tenant from before the marker shipped). A deliberate
    ///     <paramref name="overrideForeignOwnership" /> lets the restore proceed into another instance's
    ///     tenant database and logs the override as a warning naming the owner; another instance's system
    ///     database is never overwritten. Nor does the override restore and attach a database another
    ///     instance still registers: the attach guard (AB#8796) would refuse the attach after the data was
    ///     already replaced, and a successful attach would register one database in two instances.
    /// </summary>
    /// <returns>
    ///     The refusal (or null) and, when the override was granted for a database carrying another instance's
    ///     marker, the identity of that marker: the claim takes over exactly that marker and refuses any other.
    /// </returns>
    private async Task<(string? Refusal, TenantOwnershipMarkerIdentity? ApprovedForeignMarker)>
        CheckForeignOwnershipAsync(string tenantId, string databaseName, bool overrideForeignOwnership,
            bool attachTenant, CancellationToken cancellationToken)
    {
        var ownInstance = TenantDatabaseRegistrationLookup.Normalize(systemContext.DatabaseName);

        // The decision is the one attach and delete use (TenantDatabaseForeignOwnership, AB#8796); only the
        // messages are the restore's own. This instance's system database is refused here as well.
        var isOwnSystemDatabase = string.Equals(TenantDatabaseRegistrationLookup.Normalize(databaseName),
            ownInstance, StringComparison.Ordinal);
        var owner = isOwnSystemDatabase
            ? null
            : await TenantDatabaseForeignOwnership.FindAsync(tenantOwnershipStore, registrationLookup,
                systemContext.DatabaseName, databaseName, cancellationToken);

        if (isOwnSystemDatabase || owner?.IsSystemDatabase == true)
        {
            var whose = isOwnSystemDatabase ? "this instance" : "another OctoMesh instance on this MongoDB server";
            return (RefuseRestore(tenantId, databaseName,
                $"it is the system database of {whose}; the restore would overwrite that entire instance. " +
                "This refusal cannot be overridden"), null);
        }

        if (owner == null)
        {
            return (null, null);
        }

        var foreignRegistrations = owner.ForeignRegistrations;
        var marker = owner.ForeignMarker;
        var foreignOwner = marker != null
            ? $"is owned by another OctoMesh instance (owner system database " +
              $"'{marker.OwnerSystemDatabaseName}', tenant '{marker.TenantId}', stamped {marker.AttachedAtUtc:u})"
            : $"is registered as a tenant database by another OctoMesh instance (system database " +
              $"'{string.Join("', '", foreignRegistrations)}')";

        if (overrideForeignOwnership && attachTenant && foreignRegistrations.Count > 0)
        {
            return (RefuseRestore(tenantId, databaseName,
                $"it is registered as a tenant database by another OctoMesh instance (system database " +
                $"'{string.Join("', '", foreignRegistrations)}'); the ownership override cannot restore and attach " +
                "into it, because the attach would register one database in two instances. Detach the tenant in " +
                "that instance first, or restore without attaching"), null);
        }

        if (overrideForeignOwnership)
        {
            logger.LogWarning(
                "OWNERSHIP OVERRIDE: restoring tenant '{TenantId}' into database '{DatabaseName}', which {ForeignOwner}. " +
                "The administrator overrode the cross-instance ownership guard; the other instance's data in " +
                "this database is replaced", tenantId, databaseName, foreignOwner);
            return (null, marker != null
                ? new TenantOwnershipMarkerIdentity(marker.OwnerSystemDatabaseName,
                    DateTime.SpecifyKind(marker.AttachedAtUtc, DateTimeKind.Utc))
                : null);
        }

        return (RefuseRestore(tenantId, databaseName,
            $"it {foreignOwner}; the restore would overwrite the other instance's data. Detach the tenant in the " +
            "owning instance first, choose another database name, or override the ownership guard deliberately"), null);
    }

    /// <summary>
    ///     AB#8797: with a tenant-database prefix configured, a restore into a new database name without the prefix is
    ///     refused HERE, before anything is dropped or written - the attach at the end would refuse it only after
    ///     mongorestore filled the database. A restore over the tenant's own current database (a tenant from before
    ///     the prefix, same id, same name) is allowed: the returned name is exempted from the prefix rule of
    ///     the attach. Returns the refusal message (or null) and the name to exempt (or null). The caller opens the
    ///     exemption scope itself: an <see cref="AsyncLocal{T}" /> set inside this async method would not flow back.
    /// </summary>
    private async Task<(string? Refusal, string? ExemptDatabaseName)> CheckTenantDatabasePrefixAsync(string tenantId,
        string targetDatabaseName)
    {
        var prefix = systemConfiguration?.Value.TenantDatabasePrefix;
        if (TenantDatabaseNames.CarriesPrefix(prefix, targetDatabaseName))
        {
            return (null, null);
        }

        var existing = await systemContext.TryFindTenantContextAsync(tenantId);
        if (existing != null &&
            string.Equals(existing.DatabaseName, targetDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, targetDatabaseName);
        }

        var refusal = RefuseRestore(tenantId, targetDatabaseName,
            $"it does not start with this instance's tenant-database prefix '{prefix}' (System:TenantDatabasePrefix); " +
            "use a name with the prefix, or restore over the tenant's own existing database");
        return (refusal, null);
    }

    /// <summary>
    ///     A refused restore (AB#8672) returns the uniform conflict of attach and delete,
    ///     <see cref="TenantException.DatabaseNameNotAvailable" />: naming the owner instance, its tenant or its
    ///     system database to the caller would let it probe the other instances on the MongoDB server (AB#4763).
    ///     The full reason goes to the server log as a warning.
    /// </summary>
    private string RefuseRestore(string tenantId, string databaseName, string reason)
    {
        logger.LogWarning(
            "Refused the restore of tenant '{TenantId}' into database '{DatabaseName}' because {Reason}. " +
            "The caller only sees a generic conflict message.", tenantId, databaseName, reason);
        return TenantException.DatabaseNameNotAvailable(databaseName).Message;
    }
}
