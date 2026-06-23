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
    /// Clones an existing tenant into a new temporary tenant by piping a
    /// <c>mongodump</c> of the source database into a <c>mongorestore</c> against a fresh
    /// database, then attaches the result as <paramref name="tempTenantId"/>. The
    /// intermediate dump file lives under <c>Path.GetTempPath()</c> and is deleted before
    /// the call returns regardless of success.
    /// </summary>
    /// <remarks>
    /// AB#4209 Step 5 — the <c>DumpTenant --clean</c> orchestrator (bot-services PR 3)
    /// clones the source tenant via this primitive, calls the identity-services
    /// <c>cleanOverlayEntries</c> endpoint against the temp tenant, mongodumps the temp DB
    /// to the final clean archive, then drops the temp tenant. Cloning isolates the cleanup
    /// from the live tenant — no race between OIDC traffic and the strip operation.
    ///
    /// PHASE-3 MIGRATION CANDIDATE: this primitive is the engine half of the dump-clean
    /// flow that will move under <c>octo-platform-services</c> when it grows to Phase 3
    /// (blueprint orchestration). The primitive itself stays here (engine owns
    /// tenant lifecycle); the orchestration on top of it migrates.
    ///
    /// Caller is responsible for calling <c>DropChildTenantAsync</c> on the temp tenant
    /// after they're done. This method does NOT auto-cleanup the temp tenant — leaving
    /// orphan temp tenants on failure is preferable to silent data loss if the caller
    /// hasn't finished extracting what they needed.
    /// </remarks>
    /// <param name="sourceTenantId">The tenant to clone from. Must be attached.</param>
    /// <param name="tempTenantId">The new tenant ID to attach the clone as. Must not already exist.</param>
    /// <param name="tempDatabaseName">The new database name to restore the clone into. Must not already exist.</param>
    /// <param name="timeout">Timeout applied to each of the underlying dump + restore steps independently.</param>
    /// <param name="cancellationToken">Cancellation token propagated to both steps.</param>
    /// <returns>Command result indicating success or failure of the clone operation.</returns>
    Task<CommandResult> CloneTenantToTempAsync(string sourceTenantId, string tempTenantId,
        string tempDatabaseName, TimeSpan? timeout = null, CancellationToken? cancellationToken = null);
}
