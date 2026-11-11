using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

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
    ITenantLifecycleStore? tenantLifecycleStore = null,
    IOptions<OctoSystemConfiguration>? systemConfiguration = null) : ITenantBackupService
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
            var foreignOwnershipRefusal = await CheckForeignOwnershipAsync(tenantId,
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
                    // The delete has its own ownership guard (AB#8807) for the existing tenant's database. The
                    // administrator's override was checked against the restore TARGET only (AB#8672), so it
                    // covers the drop only when the existing tenant lives in that very database; a different
                    // database of the existing tenant is dropped only when the delete guard lets it go on its own.
                    var existingDatabaseIsTarget = string.Equals(
                        TenantDatabaseRegistrationLookup.Normalize(existingTenantContext.DatabaseName),
                        TenantDatabaseRegistrationLookup.Normalize(normalizedTargetDatabaseName),
                        StringComparison.Ordinal);
                    await systemContext.DropChildTenantAsync(session, tenantId,
                        overrideForeignOwnership: overrideForeignOwnership && existingDatabaseIsTarget);
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

            // The restore below runs with Drop = true against an operator-supplied database name, so it
            // silently overwrites whatever lives there. Everything else validating that name happens
            // AFTER the data is already gone (the attach at the end), which made this a second route to
            // the AB#4762 data loss. Refuse up front when the name belongs to a different tenant.
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
            if (!string.IsNullOrEmpty(effectiveSourceDatabaseName) && effectiveSourceDatabaseName != databaseName)
            {
                restoreOptions.NsFrom = $"{effectiveSourceDatabaseName}.*";
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

            // AB#4367: a mongorestore whose --nsInclude matched no namespace exits 0 with
            // "0 documents restored" and never creates the target database — turn that silent
            // no-op into a descriptive failure instead of a misleading downstream attach error.
            if (!await systemContext.IsDatabaseExistingAsync(databaseName))
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

            // AB#8672: the archive carries the ownership marker (AB#4945) of the instance it was dumped from.
            // In the restored copy that marker is stale: it would make the attach below refuse a dump taken on
            // another instance, and leave a foreign owner on a database this instance just wrote. Remove it;
            // the attach stamps this instance as owner, and a database restored without attach stays
            // unowned like any detached database.
            await tenantOwnershipStore.RemoveAsync(databaseName, cancellationToken ?? CancellationToken.None);
            // The same for the system-database marker of a dumped system database: the restored copy is a
            // tenant database and must not look like another instance's system database.
            await systemDatabaseMarkerStore.RemoveAsync(databaseName, cancellationToken ?? CancellationToken.None);

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
    private async Task<string?> CheckForeignOwnershipAsync(string tenantId, string databaseName,
        bool overrideForeignOwnership, bool attachTenant, CancellationToken cancellationToken)
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
            return RefuseRestore(tenantId, databaseName,
                $"it is the system database of {whose}; the restore would overwrite that entire instance. " +
                "This refusal cannot be overridden");
        }

        if (owner == null)
        {
            return null;
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
            return RefuseRestore(tenantId, databaseName,
                $"it is registered as a tenant database by another OctoMesh instance (system database " +
                $"'{string.Join("', '", foreignRegistrations)}'); the ownership override cannot restore and attach " +
                "into it, because the attach would register one database in two instances. Detach the tenant in " +
                "that instance first, or restore without attaching");
        }

        if (overrideForeignOwnership)
        {
            logger.LogWarning(
                "OWNERSHIP OVERRIDE: restoring tenant '{TenantId}' into database '{DatabaseName}', which {ForeignOwner}. " +
                "The administrator overrode the cross-instance ownership guard; the other instance's data in " +
                "this database is replaced", tenantId, databaseName, foreignOwner);
            return null;
        }

        return RefuseRestore(tenantId, databaseName,
            $"it {foreignOwner}; the restore would overwrite the other instance's data. Detach the tenant in the " +
            "owning instance first, choose another database name, or override the ownership guard deliberately");
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
