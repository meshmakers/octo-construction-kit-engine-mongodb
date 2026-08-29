using System.Collections.Concurrent;
using System.Diagnostics;

using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.CkModelMigrations;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.StreamData;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

[DebuggerDisplay("TenantId = {TenantId}")]
public class TenantContext : ITenantContext
{
    /// <summary>A tenant id travels in URL route segments, so keep it to an unambiguous ASCII subset.</summary>
    private const int MaxTenantIdLength = 64;

    /// <summary>MongoDB rejects database names longer than 63 bytes (64 on Windows hosts).</summary>
    private const int MaxDatabaseNameLength = 63;

    /// <summary>Characters MongoDB does not accept in a database name.</summary>
    private static readonly char[] InvalidDatabaseNameCharacters = ['/', '\\', '.', ' ', '"', '$', '*', '<', '>', ':', '|', '?'];

    /// <summary>Databases MongoDB owns. They must never be adopted as a tenant database.</summary>
    private static readonly HashSet<string> ReservedDatabaseNames =
        new(StringComparer.OrdinalIgnoreCase) { "admin", "local", "config" };

    private readonly ILogger<TenantContext> _logger;
    private readonly IBulkRtMutation _bulkRtMutation;
    private readonly ICkCacheService _cacheService;

    private readonly ILoggerFactory _loggerFactory;

    private readonly IMetricsContext _metricsContext;
    private readonly IModelLoaderService _modelLoaderService;
    private readonly IServiceProvider _serviceProvider;

    protected readonly IDatabaseCkModelRepository _ckModelRepositoryService;
    protected readonly ICatalogService _catalogService;
    protected readonly IAdminRepositoryClient _adminRepositoryClient;
    protected readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    protected readonly ITenantNotifications _tenantNotifications;

    protected TenantContext(ILoggerFactory loggerFactory, IOptions<OctoSystemConfiguration> systemConfiguration,
        IServiceProvider serviceProvider, string tenantId, string databaseName)
    {
        TenantId = tenantId;
        _metricsContext = serviceProvider.GetRequiredService<IMetricsContext>();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TenantContext>();
        _systemConfiguration = systemConfiguration;
        _serviceProvider = serviceProvider;
        DatabaseName = databaseName;
        _catalogService = serviceProvider.GetRequiredService<ICatalogService>();
        _tenantNotifications = serviceProvider.GetRequiredService<ITenantNotifications>();
        _ckModelRepositoryService = serviceProvider.GetRequiredService<IDatabaseCkModelRepository>();
        _cacheService = serviceProvider.GetRequiredService<ICkCacheService>();
        _modelLoaderService = serviceProvider.GetRequiredService<IModelLoaderService>();
        _bulkRtMutation = serviceProvider.GetRequiredService<IBulkRtMutation>();
        var adminRepositoryAccess = serviceProvider.GetRequiredService<IAdminRepositoryAccess>();
        _adminRepositoryClient = adminRepositoryAccess.GetRepositoryClient(databaseName);
    }

    /// <summary>
    /// Gets the unique identifier for the tenant.
    /// </summary>
    public string TenantId { get; }

    /// <summary>
    /// Gets the name of the database associated with the tenant.
    /// </summary>
    public string DatabaseName { get; }

    #region Transaction handling

    public async Task<IOctoAdminSession> GetAdminSessionAsync()
    {
        var adminSession = await _adminRepositoryClient.GetAdminSessionAsync();
        return adminSession;
    }

    #endregion Transaction handling

    #region Tenant management

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task LoadCacheForTenantAsync()
    {
        if (!_cacheService.IsTenantLoaded(TenantId))
        {
            var tenantRepository = GetTenantRepository();
            await tenantRepository.LoadCacheForTenantAsync(_cacheService);
        }
    }

    public async Task CreateRtAssociationIndexesAsync()
    {
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName, TenantId);
        await repositoryDataSource.CreateRtAssociationIndexesAsync();
    }

    public async Task UpdateIndexesAsync(IOctoAdminSession adminSession)
    {
        _logger.LogInformation("Updating indexes for tenant {TenantId} in database {DatabaseName}", TenantId,
            DatabaseName);

        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName, TenantId);
        await repositoryDataSource.UpdateIndexAsync(adminSession, false);

        _logger.LogInformation("Indexes updated for tenant {TenantId} in database {DatabaseName}", TenantId,
            DatabaseName);
    }

    public async Task CreateChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var normalizedDatabaseName = NormalizeDatabaseName(databaseName);
        var normalizedTenantId = tenantId.NormalizeString();

        Guid correlationId = Guid.NewGuid();

        // Validate both namespaces BEFORE anything with a side effect runs - including the pre-create
        // notification. Everything below this point is inside the try whose catch rolls the tenant
        // back by dropping the database and its user, so a precondition failure raised down there
        // destroys a database that belongs to somebody else (AB#4762).
        await EnsureTenantNamespaceAvailableAsync(adminSession, normalizedTenantId, normalizedDatabaseName,
            TenantNamespaceMode.CreateNewDatabase, correlationId);

        // Guards the destructive rollback below: it stays false until we have provably created the
        // database ourselves. Nothing that runs before it is set may ever reach the drop.
        var databaseCreated = false;

        try
        {
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(tenantId, correlationId);

            // Create the database. Throws when the name was taken between the check above and here,
            // while databaseCreated is still false - so the racer's database is never dropped.
            await CreateTenantInternalAsync(normalizedDatabaseName);
            databaseCreated = true;

            // AB#4945: stamp the ownership marker right after the physical create, so the database
            // is owned by this instance from birth. A stamp failure fails the create; the rollback
            // below drops the database we provably created.
            await GetTenantOwnershipStore().StampAsync(normalizedDatabaseName, normalizedTenantId,
                OwnerInstanceIdentity);

            // Restore the tenant system model on the newly created repository
            await UpdateSystemCkModelAsync(normalizedDatabaseName, normalizedTenantId, true);

            // Add the new tenant as child tenant of the current one
            if (TenantId != _systemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var rtTenant = new RtTenant { TenantId = normalizedTenantId, DatabaseName = normalizedDatabaseName };

                var tenantRepository = GetTenantRepositoryAsAdmin();
                await tenantRepository.InsertOneRtEntityAsync(adminSession, rtTenant);
            }

            // Add the new tenant in system tenant to be found in future operations
            var rtSystemTenant = new RtTenant
            {
                TenantId = normalizedTenantId, ParentTenantId = TenantId, DatabaseName = normalizedDatabaseName
            };
            var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
            await systemTenantRepository.InsertOneRtEntityAsync(adminSession, rtSystemTenant);

            // Distribute updates (post) only on success. Signalling "tenant created" while we are
            // about to roll the tenant back leaves other services provisioning orphaned resources
            // for a tenant that never came to exist (AB#1958).
            await _tenantNotifications.NotifyPosTenantCreateAsync(tenantId, correlationId);
        }
        catch (Exception ex)
        {
            // Roll back the database + database user, but ONLY when this operation created them.
            // Dropping a database we merely found would destroy another tenant's data, and dropping
            // its user would revoke that tenant's access (AB#4762). The octosystem tenant entries are
            // part of the caller's transaction and are rolled back when it is aborted (AB#1958).
            await CleanupFailedTenantCreationAsync(normalizedDatabaseName, tenantId, correlationId, ex,
                dropDatabaseAndUser: databaseCreated);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<BlueprintApplicationResult?> CreateChildTenantAsync(IOctoAdminSession adminSession,
        string databaseName, string tenantId, BlueprintId? blueprintId)
    {
        // First, create the tenant using the standard method
        await CreateChildTenantAsync(adminSession, databaseName, tenantId);

        // If no blueprint specified, return null
        if (blueprintId == null)
        {
            return null;
        }

        // Apply the blueprint to the newly created tenant
        _logger.LogInformation("Applying blueprint {BlueprintId} to new tenant {TenantId}",
            blueprintId, tenantId);

        try
        {
            var blueprintService = _serviceProvider.GetService<IBlueprintService>();
            if (blueprintService == null)
            {
                _logger.LogWarning(
                    "IBlueprintService is not registered. Blueprint {BlueprintId} will not be applied to tenant {TenantId}",
                    blueprintId, tenantId);

                var operationResult = new OperationResult();
                operationResult.AddMessage(new ConstructionKit.Contracts.Messages.OperationMessage(
                    ConstructionKit.Contracts.Messages.MessageLevel.Error, null, 1,
                    "IBlueprintService is not registered. Use AddBlueprintSupport() to register blueprint services."));

                return BlueprintApplicationResult.Failed(operationResult);
            }

            var result = await blueprintService.ApplyBlueprintAsync(tenantId, blueprintId);

            if (!result.IsSuccess)
            {
                _logger.LogError("Failed to apply blueprint {BlueprintId} to tenant {TenantId}. Rolling back tenant creation",
                    blueprintId, tenantId);

                // Rollback: Drop the tenant
                await DropChildTenantAsync(adminSession, tenantId);

                throw new InvalidOperationException(
                    $"Failed to apply blueprint '{blueprintId}' to tenant '{tenantId}'. Tenant creation has been rolled back. " +
                    $"Errors: {string.Join(", ", result.OperationResult.Messages.Select(m => m.MessageText))}");
            }

            _logger.LogInformation(
                "Blueprint {BlueprintId} applied successfully to tenant {TenantId}: {EntitiesCreated} entities created",
                blueprintId, tenantId, result.EntitiesCreated);

            return result;
        }
        catch (InvalidOperationException)
        {
            throw; // Re-throw our custom exception
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying blueprint {BlueprintId} to tenant {TenantId}. Rolling back tenant creation",
                blueprintId, tenantId);

            // Rollback: Drop the tenant
            await DropChildTenantAsync(adminSession, tenantId);

            throw new InvalidOperationException(
                $"Failed to apply blueprint '{blueprintId}' to tenant '{tenantId}'. Tenant creation has been rolled back.",
                ex);
        }
    }

    protected async Task UpdateSystemCkModelAsync(string normalizedDatabaseName, string tenantId, bool isRepositoryInCreation = false)
    {
        var databaseContext = CreateRepositoryDataSourceAsAdmin(normalizedDatabaseName, tenantId);
        var databaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, databaseContext, tenantId);
        OperationResult operationResult = new();
        if (await _ckModelRepositoryService.IsExistingAsync(SystemCkIds.CkModelId, databaseSourceIdentifier))
        {
            return;
        }

        // If either the database not exist or the model already exist, we do nothing.
        if (!isRepositoryInCreation && (!await IsDatabaseExistingAsync(normalizedDatabaseName)))
        {
            return;
        }

        // Never seed the System CK model into an infrastructure-only shell of the SYSTEM database
        // (AB#4854): the shell has no datasource user, and a model seeded here (as admin) makes
        // IsSystemTenantExistingAsync treat the shell as a real system database — the bootstrap, the
        // only legitimate creator of the datasource user, would then be skipped forever. Checked
        // here, at the seed decision itself, so a shell that materializes after a caller's earlier
        // probe (EnsureSystemCkModelAsync saw no database at all) cannot slip through a
        // check-then-act window.
        if (!isRepositoryInCreation
            && normalizedDatabaseName == NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName)
            && await IsDatabaseMaterializedOnlyByInfrastructureAsync(normalizedDatabaseName))
        {
            return;
        }

        // Capture schema versions BEFORE updating (for migration detection)
        // Note: We read directly from the database to avoid recursion through IRuntimeRepositoryProvider
        // which would call TryFindTenantContextAsync and trigger UpdateSystemCkModelAsync again
        IReadOnlyDictionary<string, string>? previousSchemaVersions = null;
        if (!isRepositoryInCreation)
        {
            previousSchemaVersions = await GetSchemaVersionsDirectAsync(databaseSourceIdentifier);
        }

        var correlationId = Guid.NewGuid();
        try
        {
            _logger.LogInformation("Restoring system CK Model into tenant '{TenantId}'", TenantId);

            if (!isRepositoryInCreation)
            {
                await _tenantNotifications.NotifyPreTenantUpdateAsync(TenantId, correlationId);
            }

            var ckCompiledModelRoot =
                await _catalogService.GetAsync(SystemCkIds.CkModelId, operationResult);
            if (ckCompiledModelRoot == null)
            {
                throw TenantException.SystemModelNotFoundInCatalog(SystemCkIds.CkModelId);
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw TenantException.ErrorDuringSystemModelLoad(operationResult);
            }

            try
            {
                await _ckModelRepositoryService.UpdateModelAsync(
                    ckCompiledModelRoot, databaseSourceIdentifier);
            }
            catch (ModelValidationException ex)
            {
                // Gracefully handle missing dependencies - this can happen when services start
                // in parallel and a dependent CK model is still being imported by another service.
                // A RabbitMQ tenant update notification will be sent when the dependency is ready,
                // allowing this update to succeed on the next attempt.
                _logger.LogWarning(
                    "Skipping System CK model update for tenant '{TenantId}' due to missing dependencies: {Message}. " +
                    "This update will be retried when the dependent CK model becomes available.",
                    TenantId, ex.Message);
                return;
            }

            // Run migrations after updating the System CK model
            await RunSystemCkModelMigrationsAsync(tenantId, previousSchemaVersions);

            // Invalidate cache so the next access triggers a fresh load with all currently-available models.
            // Use tenantId parameter (not TenantId property) because this method is called for child tenants too.
            if (_cacheService.IsTenantLoaded(tenantId))
            {
                _cacheService.Unload(tenantId);
            }

            // Only send the notification after a successful update.
            // Sending this in a finally block would trigger other services to re-process
            // even on failures, causing an import loop.
            if (!isRepositoryInCreation)
            {
                await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
            }

            _logger.LogInformation("System CK Model restored into tenant '{TenantId}'", TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore system CK Model into tenant '{TenantId}'", TenantId);
            throw;
        }
    }

    /// <summary>
    /// Runs CK model migrations for the System model after it has been updated.
    /// </summary>
    private Task RunSystemCkModelMigrationsAsync(
        string tenantId,
        IReadOnlyDictionary<string, string>? previousSchemaVersions)
    {
        return RunCkModelMigrationsForImportAsync(tenantId, SystemCkIds.CkModelId, previousSchemaVersions);
    }

    /// <summary>
    /// Runs CK model migrations for a specific model after it has been imported or updated.
    /// </summary>
    private async Task RunCkModelMigrationsForImportAsync(
        string tenantId,
        CkModelId importedModelId,
        IReadOnlyDictionary<string, string>? previousSchemaVersions)
    {
        var ckModelUpgradeService = _serviceProvider.GetService<ICkModelUpgradeService>();
        if (ckModelUpgradeService == null)
        {
            _logger.LogDebug("CK model upgrade service not available, skipping migrations for {CkModelId}",
                importedModelId);
            return;
        }

        if (previousSchemaVersions == null || previousSchemaVersions.Count == 0)
        {
            _logger.LogDebug("No previous schema versions captured, skipping migrations for {CkModelId}",
                importedModelId);
            return;
        }

        var modelRange = importedModelId.ToVersionRange();

        _logger.LogInformation(
            "Running CK model migrations for '{CkModelId}' in tenant '{TenantId}'",
            importedModelId, tenantId);

        var result = await ckModelUpgradeService.UpgradeModelsAsync(
            tenantId,
            new[] { modelRange },
            new CkMigrationOptions { ContinueOnError = false },
            previousSchemaVersions,
            CancellationToken.None);

        if (!result.Success)
        {
            _logger.LogError(
                "CK model migration failed for '{CkModelId}' in tenant '{TenantId}': {Errors}",
                importedModelId, tenantId, string.Join("; ", result.Errors));
        }
        else if (result.TotalEntitiesAffected > 0)
        {
            _logger.LogInformation(
                "CK model migration completed for '{CkModelId}' in tenant '{TenantId}': {EntitiesAffected} entities affected",
                importedModelId, tenantId, result.TotalEntitiesAffected);
        }
    }

    /// <summary>
    /// Checks for and retries any pending CK model migrations for an already-imported model.
    /// This handles the case where a previous migration attempt failed (e.g., due to a transaction
    /// error), leaving the MigrationHistory at an older version while the CkModel schema is already
    /// at the target version. Without this check, the model would pass the IsExistingAsync gate
    /// on subsequent startups and the failed migration would never be retried.
    /// </summary>
    private async Task RetryPendingMigrationsAsync(CkModelId ckModelId)
    {
        var ckModelUpgradeService = _serviceProvider.GetService<ICkModelUpgradeService>();
        if (ckModelUpgradeService == null)
        {
            return;
        }

        var modelRange = ckModelId.ToVersionRange();

        // Pass null for previousSchemaVersions so the upgrade service uses MigrationHistory
        // as the source of truth. If we passed the current schema version, it would override
        // the MigrationHistory version and skip the migration.
        var result = await ckModelUpgradeService.UpgradeModelsAsync(
            TenantId,
            new[] { modelRange },
            new CkMigrationOptions { ContinueOnError = false },
            previouslyInstalledVersions: null,
            CancellationToken.None);

        if (!result.Success)
        {
            _logger.LogError(
                "Retry of pending CK model migration failed for '{CkModelId}' in tenant '{TenantId}': {Errors}",
                ckModelId, TenantId, string.Join("; ", result.Errors));
        }
        else if (result.TotalEntitiesAffected > 0)
        {
            _logger.LogInformation(
                "Pending CK model migration completed for '{CkModelId}' in tenant '{TenantId}': {EntitiesAffected} entities affected",
                ckModelId, TenantId, result.TotalEntitiesAffected);
        }
    }

    /// <summary>
    /// Gets schema versions directly from the database without going through IRuntimeRepositoryProvider.
    /// This avoids recursion when called during UpdateSystemCkModelAsync.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetSchemaVersionsDirectAsync(
        TenantDatabaseSourceIdentifier databaseSourceIdentifier)
    {
        var versions = new Dictionary<string, string>();

        try
        {
            var session = await databaseSourceIdentifier.MongoDbRepositoryDataSource.CreateSessionAsync();
            try
            {
                // Query all available CK models directly from the database
                var ckModels = await databaseSourceIdentifier.MongoDbRepositoryDataSource.CkModels
                    .FindManyAsync(session, model => model.ModelState == ModelState.Available);

                foreach (var model in ckModels)
                {
                    versions[model.ModelId] = model.Id.Version.ToString();
                    _logger.LogDebug(
                        "Found schema version {Version} for CK model {ModelName} (direct read)",
                        model.Id.Version, model.ModelId);
                }
            }
            finally
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting schema versions directly from database");
        }

        return versions;
    }

    /// <summary>
    /// Reads the declared display rules of all available CK types directly from the database
    /// (AB#4812). Mirrors <see cref="GetSchemaVersionsDirectAsync" />: called before and after a
    /// model import to detect rule changes, because the import hard-deletes the previous version's
    /// CkType documents (there is no old-model snapshot afterwards).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, DisplayRules.DeclaredDisplayRules>>
        GetDeclaredDisplayRulesDirectAsync(TenantDatabaseSourceIdentifier databaseSourceIdentifier)
    {
        var rules = new Dictionary<string, DisplayRules.DeclaredDisplayRules>();

        try
        {
            var session = await databaseSourceIdentifier.MongoDbRepositoryDataSource.CreateSessionAsync();
            try
            {
                var ckTypes = await databaseSourceIdentifier.MongoDbRepositoryDataSource.CkTypes
                    .FindManyAsync(session, type => type.ModelState == ModelState.Available);

                foreach (var ckType in ckTypes)
                {
                    rules[ckType.CkTypeId.FullName] =
                        new DisplayRules.DeclaredDisplayRules(ckType.DisplayNameRule, ckType.DisplayDescriptionRule);
                }
            }
            finally
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting declared display rules directly from database");
        }

        return rules;
    }

    /// <summary>
    /// Diffs the declared display rules before/after a model import and enqueues a backfill sweep
    /// task per changed type (AB#4812). Failures are logged but never fail the import — the sweep
    /// is a repair mechanism, not part of the import contract.
    /// </summary>
    private async Task EnqueueDisplayRuleSweepsAsync(
        IReadOnlyDictionary<string, DisplayRules.DeclaredDisplayRules> rulesBeforeImport,
        TenantDatabaseSourceIdentifier databaseSourceIdentifier)
    {
        try
        {
            var sweepStore = _serviceProvider.GetService<Contracts.MongoDb.DisplayRules.IDisplayRuleSweepStore>();
            if (sweepStore == null)
            {
                return;
            }

            var rulesAfterImport = await GetDeclaredDisplayRulesDirectAsync(databaseSourceIdentifier);
            var changedTypeIds =
                DisplayRules.DisplayRuleChangeDetector.GetChangedTypeIds(rulesBeforeImport, rulesAfterImport);
            foreach (var changedTypeId in changedTypeIds)
            {
                await sweepStore.EnqueueAsync(TenantId, changedTypeId);
                _logger.LogInformation(
                    "Display rules of type '{CkTypeId}' changed on import; backfill sweep enqueued for tenant '{TenantId}'",
                    changedTypeId, TenantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enqueue display rule backfill sweeps for tenant '{TenantId}' — existing entities keep stale display names until their next save",
                TenantId);
        }
    }

    /// <summary>
    ///     Creates the tenant database and its database user.
    /// </summary>
    /// <param name="normalizedDatabaseName">Database name, already normalized by the caller.</param>
    /// <param name="allowInfrastructureMaterializedDatabase">
    ///     When true, a database that was materialized only by the engine's own infrastructure
    ///     collections does not count as taken. Only the system-tenant bootstrap passes true: on a
    ///     virgin server the engine's plumbing (lifecycle probe index, a setup-retry record from an
    ///     earlier failed attempt) can create the system database as an empty shell before the
    ///     bootstrap runs, and refusing to bootstrap over that shell wedged every fresh install
    ///     (AB#4854). Child creates keep the strict guard — for them any existing database, shell or
    ///     not, is another tenant's namespace.
    /// </param>
    /// <remarks>
    ///     Callers must have validated availability up front via
    ///     <see cref="EnsureTenantNamespaceAvailableAsync" />. The existence check kept here is only a
    ///     defence-in-depth net against a concurrent create materializing the database in between:
    ///     without it, two racing creates would both proceed and silently share one database. It
    ///     deliberately throws before the caller marks the database as created, so the rollback can
    ///     never drop a database this operation did not create (AB#4762).
    /// </remarks>
    protected async Task CreateTenantInternalAsync(string normalizedDatabaseName,
        bool allowInfrastructureMaterializedDatabase = false)
    {
        ArgumentValidation.ValidateString(nameof(normalizedDatabaseName), normalizedDatabaseName);

        if (await IsDatabaseExistingAsync(normalizedDatabaseName)
            && !(allowInfrastructureMaterializedDatabase
                 && await IsDatabaseMaterializedOnlyByInfrastructureAsync(normalizedDatabaseName)))
        {
            throw TenantException.DatabaseNameNotAvailable(normalizedDatabaseName);
        }

        await _adminRepositoryClient.CreateRepositoryAsync(normalizedDatabaseName);
        await _adminRepositoryClient.CreateUser(_systemConfiguration.Value.AuthenticationDatabaseName,
            normalizedDatabaseName, string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName),
            _systemConfiguration.Value.DatabaseUserPassword);
    }

    /// <summary>
    ///     Whether the given database contains nothing but the engine's own infrastructure
    ///     collections (see <see cref="InfrastructureCollections" />) — an "infrastructure-only
    ///     shell", typically materialized by an index creation or a setup-retry record on a virgin
    ///     server before the system tenant was bootstrapped (AB#4854). A non-existing database
    ///     counts as a shell too (no collections at all).
    /// </summary>
    protected async Task<bool> IsDatabaseMaterializedOnlyByInfrastructureAsync(string databaseName)
    {
        var collectionNames = await _adminRepositoryClient.ListCollectionNamesAsync(databaseName);
        return collectionNames.All(InfrastructureCollections.IsInfrastructure);
    }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task AttachChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var normalizedDatabaseName = NormalizeDatabaseName(databaseName);
        var normalizedTenantId = tenantId.NormalizeString();

        Guid correlationId = Guid.NewGuid();

        // Attach shares both namespaces with create, so it goes through the same gate: it must not be
        // able to claim the system tenant id, the system database, or a database that another tenant
        // is already registered against (AB#4763). The gate also rejects a database owned by another
        // instance (AB#4945), so any marker still present after it belongs to this instance.
        await EnsureTenantNamespaceAvailableAsync(adminSession, normalizedTenantId, normalizedDatabaseName,
            TenantNamespaceMode.AttachExistingDatabase, correlationId);

        // Tracked for the rollback below: a marker we created on a failed attach must be removed
        // again, or the database would stay locked for every other instance even though it was
        // never attached. A marker that predates this call (an earlier failed attach of this
        // instance, or a detach that crashed mid-way) is left alone — it is ours and self-heals.
        var hadOwnMarkerBefore = await GetTenantOwnershipStore().GetAsync(normalizedDatabaseName) != null;

        try
        {
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(tenantId, correlationId);

            // AB#4945: claim the database for this instance before anything else in the try — the
            // marker is the cross-instance lock, so the claim window should be as small as
            // possible. The registry rows below roll back with the caller's transaction; the
            // marker is compensated in the catch.
            await GetTenantOwnershipStore().StampAsync(normalizedDatabaseName, normalizedTenantId,
                OwnerInstanceIdentity);

            // Add the new tenant as child tenant of the current one
            if (TenantId != _systemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                // Normalized, like the system-registry record below: every lookup normalizes, so a
                // mixed-case attach used to write a record its own reads could never find.
                var octoTenant = new RtTenant
                {
                    TenantId = normalizedTenantId, DatabaseName = normalizedDatabaseName
                };

                var tenantRepository = GetTenantRepositoryAsAdmin();
                await tenantRepository.InsertOneRtEntityAsync(adminSession, octoTenant);
            }

            await _adminRepositoryClient.CreateUser(_systemConfiguration.Value.AuthenticationDatabaseName,
                normalizedDatabaseName, string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName),
                _systemConfiguration.Value.DatabaseUserPassword);

            // Add the new tenant in system tenant to be found in future operations
            var rtSystemTenant = new RtTenant
            {
                TenantId = normalizedTenantId, ParentTenantId = TenantId, DatabaseName = normalizedDatabaseName
            };
            var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
            await systemTenantRepository.InsertOneRtEntityAsync(adminSession, rtSystemTenant);

            // Distribute updates (post) only on success - see CreateChildTenantAsync (AB#1958).
            await _tenantNotifications.NotifyPosTenantCreateAsync(tenantId, correlationId);
        }
        catch (Exception ex)
        {
            // AB#4945: remove the ownership marker again when THIS call created it — otherwise a
            // failed attach leaves the database locked for every other instance without any
            // registry row backing the claim. Best-effort; a leftover marker of this instance
            // self-heals on the next attach by this instance.
            if (!hadOwnMarkerBefore)
            {
                await GetTenantOwnershipStore().TryRemoveAsync(normalizedDatabaseName);
            }

            // Attach reuses an already existing database, so it is never dropped here; only the
            // database user we may have created is rolled back, plus an event-log entry. The
            // octosystem tenant entries roll back with the caller's transaction (AB#1958).
            await CleanupFailedTenantCreationAsync(normalizedDatabaseName, tenantId, correlationId, ex,
                dropDatabaseAndUser: false);
            throw;
        }
    }

    /// <summary>
    ///     Rolls back the side effects of a failed tenant creation and records the failure in the
    ///     platform event log (AB#1958).
    /// </summary>
    /// <param name="normalizedDatabaseName">The (normalized) tenant database name.</param>
    /// <param name="tenantId">The tenant that failed to be created.</param>
    /// <param name="correlationId">Correlation id of the create operation.</param>
    /// <param name="cause">The exception that aborted the creation.</param>
    /// <param name="dropDatabaseAndUser">
    ///     <c>true</c> for create (the database/user were created by us and must be removed);
    ///     <c>false</c> for attach (the database pre-existed and must be kept).
    /// </param>
    protected async Task CleanupFailedTenantCreationAsync(string normalizedDatabaseName, string tenantId,
        Guid correlationId, Exception cause, bool dropDatabaseAndUser)
    {
        _logger.LogError(cause,
            "Tenant creation failed for tenant {TenantId} (database {DatabaseName}); performing cleanup",
            tenantId, normalizedDatabaseName);

        if (dropDatabaseAndUser)
        {
            try
            {
                await _adminRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to drop database {DatabaseName} during tenant creation rollback",
                    normalizedDatabaseName);
            }

            try
            {
                await _adminRepositoryClient.DropUser(_systemConfiguration.Value.AuthenticationDatabaseName,
                    string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to drop database user during tenant creation rollback for database {DatabaseName}",
                    normalizedDatabaseName);
            }
        }

        // Surface the failure in the platform event log. Routed through IAuditEventSink so the host
        // (asset-repo) persists it via EventRepositoryAuditEventSink; engine-only hosts just log it.
        try
        {
            var auditSink = _serviceProvider.GetService<IAuditEventSink>();
            if (auditSink != null)
            {
                await auditSink.PublishAsync(new AuditEvent(
                    null,
                    AuditEventLevel.Error,
                    "Tenant.CreateFailed",
                    $"Creation of tenant '{tenantId}' failed and was rolled back. Reason: {cause.Message}")
                {
                    Metadata = new Dictionary<string, object?>
                    {
                        ["tenantId"] = tenantId,
                        ["databaseName"] = normalizedDatabaseName,
                        ["correlationId"] = correlationId
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write tenant-create-failure event for tenant {TenantId}", tenantId);
        }
    }

    public async Task DetachChildTenantAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var octoTenant = await GetRtTenantAsync(adminSession, tenantId);
        if (octoTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        Guid correlationId = Guid.NewGuid();

        try
        {
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantDeleteAsync(tenantId, correlationId);

            var tenantRepository = GetTenantRepositoryAsAdmin();
            await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                TenantRegistryFilter(tenantId, octoTenant.DatabaseName), DeleteOptions.Erase);

            // Detach must be the exact inverse of attach across BOTH registries. Leaving the
            // platform-wide record behind kept a "detached" tenant fully resolvable from the system
            // context, and once the uniqueness check became global it would also have blocked every
            // re-attach of that tenant id (AB#4763). The system registry has always been written
            // normalized, so its filter takes the normalized name — unlike the local delete above,
            // which must match the record's stored (possibly legacy raw-cased) value.
            if (TenantId != _systemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
                await systemTenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                    TenantRegistryFilter(tenantId, NormalizeDatabaseName(octoTenant.DatabaseName)),
                    DeleteOptions.Erase);
            }

            // AB#4945: detach is the one sanctioned ownership handover (the attach guard is strict,
            // no force override) — removing the marker releases the database for any instance.
            // Deliberately LAST and not best-effort: if the removal throws, the caller's transaction
            // aborts, the registry rows are restored, and the database stays consistently attached
            // here. The marker delete itself is non-transactional, but a marker removed just before
            // a registry rollback only means the tenant is re-claimable — the next resolve of this
            // instance re-stamps it lazily.
            await GetTenantOwnershipStore().RemoveAsync(NormalizeDatabaseName(octoTenant.DatabaseName));
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await _tenantNotifications.NotifyPosTenantDeleteAsync(tenantId, correlationId);
        }
    }

    /// <summary>
    ///     Filter that identifies exactly one registry record.
    /// </summary>
    /// <remarks>
    ///     Qualified with the database name on purpose. A delete filtered on the tenant id alone
    ///     removes an arbitrary match, so in an environment still holding the duplicates AB#4763
    ///     produced, one parent's detach or delete could unregister a different parent's live tenant.
    ///     The database name is chosen over ParentTenantId because it is populated on every record,
    ///     including ones written before ParentTenantId existed.
    ///     <para>
    ///     The database name is matched VERBATIM, not normalized: the comparison is case-sensitive
    ///     and the caller's value comes from the very record the filter has to re-identify. The
    ///     pre-AB#4763 attach wrote the operator's raw casing into the subtree-local registry, so
    ///     normalizing here made the local delete silently miss every such legacy record — the
    ///     tenant then survived its own deletion. Callers pass the value as stored in the registry
    ///     they are deleting from (raw from the local record; normalized for the system registry,
    ///     which has always been written normalized).
    ///     </para>
    /// </remarks>
    private static FieldFilterCriteria TenantRegistryFilter(string tenantId, string databaseNameAsStored)
    {
        return FieldFilterCriteria.Create()
            .FieldEquals(nameof(RtTenant.TenantId), tenantId.NormalizeString())
            .FieldEquals(nameof(RtTenant.DatabaseName), databaseNameAsStored);
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearChildTenantAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(adminSession, tenantId);
        if (octoTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        Guid correlationId = Guid.NewGuid();

        try
        {
            await _tenantNotifications.NotifyPreTenantUpdateAsync(tenantId, correlationId);

            // Clear empties the tenant: its archive entities go with the database, so their stream
            // data tables go too - keeping them would only orphan them (AB#4255).
            await DropChildTenantAsync(adminSession, tenantId, dropStreamData: true);
            await CreateChildTenantAsync(adminSession, octoTenant.DatabaseName, tenantId);
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantUpdateAsync(tenantId, correlationId);
        }
    }

    /// <summary>
    ///     Drops the stream data (CrateDB) tables of the archives collected into the deletion handle,
    ///     after the tenant database is gone (AB#4255). Exactly those archives' tables - the CrateDB
    ///     schema is shared by tenants whose ids differ only in <c>-</c>/<c>_</c>, so a schema-wide
    ///     drop would take another tenant's data. The Activated-archive guard in
    ///     <see cref="DisableStreamDataAsync" /> only ensures nothing is live at this point; the
    ///     tables of Disabled/Failed archives are still there. Best-effort like the user drop: the
    ///     tenant is already deleted, so a failure is logged with the tables that have to be dropped
    ///     by hand (every statement is idempotent). Skipped when no stream data backend is registered
    ///     or stream data is disabled at instance level (no CrateDB configured).
    /// </summary>
    private async Task DropStreamDataArchiveTablesAsync(string tenantId, string databaseName,
        IReadOnlyList<OctoObjectId> archives)
    {
        if (archives.Count == 0)
        {
            return;
        }

        var factory = _serviceProvider.GetService<IStreamDataRepositoryFactory>();
        var instanceConfig = _serviceProvider.GetService<IOptions<StreamDataInstanceConfiguration>>();
        if (factory is null || instanceConfig?.Value.Enabled != true)
        {
            _logger.LogWarning(
                "Dropped database {DatabaseName} of tenant {TenantId}; the stream data tables of its {Count} " +
                "archive(s) were left alone because no stream data backend is enabled on this instance: {Tables}",
                databaseName, tenantId, archives.Count, DescribeArchiveTables(archives));
            return;
        }

        try
        {
            await factory.DeleteArchiveTablesAsync(tenantId, archives);
            _logger.LogInformation("Dropped the stream data tables of {Count} archive(s) of tenant {TenantId}",
                archives.Count, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dropped database {DatabaseName} of tenant {TenantId} but failed to drop the stream data (CrateDB) " +
                "tables of its archives; drop them manually in the tenant's schema (the __genmap side-table " +
                "exists only for rollup archives): {Tables}", databaseName, tenantId, DescribeArchiveTables(archives));
        }
    }

    /// <summary>Both table names of every archive, so the manual remediation in the log is complete.</summary>
    private static string DescribeArchiveTables(IReadOnlyList<OctoObjectId> archives)
    {
        return string.Join(", ", archives.Select(rtId => $"archive_{rtId}, archive_{rtId}__genmap"));
    }

    /// <summary>
    ///     The ids of every archive of the child tenant - all statuses, a Created archive's
    ///     <c>DROP TABLE IF EXISTS</c> is harmless and a Failed one may own a partial table - or an
    ///     empty list when the tenant does not exist or never imported the System.StreamData model
    ///     (without the model no archive entity can exist, and enumerating the store would throw
    ///     CkCacheException). Read failures propagate: called before anything is deleted, so an
    ///     unreadable tenant fails the delete instead of leaking its tables.
    /// </summary>
    private async Task<IReadOnlyList<OctoObjectId>> CollectStreamDataArchivesAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var child = await TryGetChildTenantContextAsync(adminSession, tenantId);
        if (child is null || !_cacheService.TryGetRtCkType(child.TenantId, ArchiveRtCkTypeId, out _))
        {
            return [];
        }

        var archives = new List<OctoObjectId>();
        await foreach (var archive in child.GetArchiveRuntimeStore().EnumerateAsync())
        {
            archives.Add(archive.RtId);
        }

        return archives;
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task DropChildTenantAsync(IOctoAdminSession adminSession, string tenantId, bool dropStreamData = false)
    {
        // Single-call convenience: delete the metadata and drop the database in one go, within the
        // caller's transaction. Suitable for callers that have no concurrent tenant-resolve to race
        // against (create-rollback, tenant backup temp cleanup, tests). Race-sensitive callers that
        // delete a live tenant (e.g. the tenant delete REST endpoint) must instead call
        // DeleteChildTenantMetadataAsync, commit, then DropTenantDatabaseAsync so the physical drop
        // happens after the record deletion is durably gone. See DeleteChildTenantMetadataAsync.
        var handle = await DeleteChildTenantMetadataAsync(adminSession, tenantId, dropStreamData);
        await DropTenantDatabaseAsync(handle, tenantId);
    }

    /// <inheritdoc />
    public async Task<TenantDeletionHandle> DeleteChildTenantMetadataAsync(IOctoAdminSession adminSession,
        string tenantId, bool dropStreamData = false)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenantRepository = GetTenantRepositoryAsAdmin();

        var octoTenant = await GetRtTenantAsync(adminSession, tenantId);
        if (octoTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        // Collected BEFORE the record deletion: this is the last moment the child resolves, and the
        // archive entities are gone with its database. Only when the caller drops the tenant for good;
        // a database swap (restore over an existing tenant) keeps the archives and their tables.
        var streamDataArchives = dropStreamData
            ? await CollectStreamDataArchivesAsync(adminSession, tenantId)
            : [];

        Guid correlationId = Guid.NewGuid();

        await _tenantNotifications.NotifyPreTenantDeleteAsync(tenantId, correlationId);

        // Deletes the tenant entry from the current tenant. Qualified with the database name so a
        // leftover duplicate tenant id cannot make this remove a different parent's record (AB#4763).
        // The stored value is passed verbatim — a legacy attach record may hold the operator's raw
        // casing, and a normalized filter would silently miss it (see TenantRegistryFilter).
        await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
            TenantRegistryFilter(tenantId, octoTenant.DatabaseName), DeleteOptions.Erase);

        // If the current tenant is not the system tenant, we need to delete the tenant entry in system
        // tenant too. That registry has always been written normalized, so it filters normalized.
        if (TenantId != _systemConfiguration.Value.SystemTenantId.NormalizeString())
        {
            var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
            await systemTenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                TenantRegistryFilter(tenantId, NormalizeDatabaseName(octoTenant.DatabaseName)),
                DeleteOptions.Erase);
        }

        return new TenantDeletionHandle(octoTenant.DatabaseName, correlationId, streamDataArchives);
    }

    /// <inheritdoc />
    public async Task DropTenantDatabaseAsync(TenantDeletionHandle handle, string tenantId)
    {
        ArgumentValidation.Validate(nameof(handle), handle);
        ArgumentValidation.ValidateString(nameof(handle.DatabaseName), handle.DatabaseName);

        // dropDatabase is case-sensitive while the existence check compares case-insensitively, so a
        // record holding a mixed-case name used to drop nothing at all - and the orphaned database
        // then permanently blocked its own name behind a deliberately reason-free conflict (AB#4762).
        var normalizedDatabaseName = NormalizeDatabaseName(handle.DatabaseName);

        try
        {
            // Drop BOTH spellings: databases from the create path are physically normalized, but a
            // legacy attach adopted the physical database under whatever casing the record holds.
            // MongoDB forbids two databases differing only in case, so at most one of the two
            // exists — and the registry-qualified metadata delete guarantees it is this tenant's.
            // dropDatabase on an absent name is a silent no-op.
            await _adminRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            if (!string.Equals(handle.DatabaseName, normalizedDatabaseName, StringComparison.Ordinal))
            {
                await _adminRepositoryClient.DropRepositoryAsync(handle.DatabaseName);
            }

            // Drop the tenant's database user too. dropDatabase does NOT remove it — the account lives
            // in the authentication database, so every delete used to leave a live credential behind
            // whose database was gone, and a database later re-created under the same name would
            // silently inherit it. Best-effort: a failure here must not fail the delete, which has
            // already removed the tenant.
            try
            {
                await _adminRepositoryClient.DropUser(_systemConfiguration.Value.AuthenticationDatabaseName,
                    string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Dropped database {DatabaseName} of tenant {TenantId} but failed to drop its database user; " +
                    "the credential has to be removed manually", normalizedDatabaseName, tenantId);
            }

            await DropStreamDataArchiveTablesAsync(tenantId, normalizedDatabaseName, handle.StreamDataArchives);
        }
        finally
        {
            // Dropping the database invalidates every connection already open in this process's cached
            // clients for it. Throw them away so a tenant re-created under the same name does not
            // inherit a pool that can only answer "requires authentication" (AB#4690). Other processes
            // do the same from the tenant lifecycle events.
            await InvalidateTenantRepositoryClientsAsync(tenantId, normalizedDatabaseName).ConfigureAwait(false);

            await _tenantNotifications.NotifyPosTenantDeleteAsync(tenantId, handle.CorrelationId);
        }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsChildTenantExistingAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(adminSession, tenantId);
        return octoTenant != null;
    }

    /// <summary>
    ///     Whether the caller intends to create a new database or to adopt an existing one.
    /// </summary>
    private enum TenantNamespaceMode
    {
        CreateNewDatabase,
        AttachExistingDatabase
    }

    /// <summary>
    ///     Single authority for both platform-wide namespaces: the tenant id and the database name.
    ///     Throws a conflict <see cref="TenantException" /> when either is unavailable.
    /// </summary>
    /// <remarks>
    ///     Must be called before any side effect. Both conflicts surface as a deliberately uniform,
    ///     reason-free message, because a caller may have neither knowledge of nor access to the
    ///     colliding resource; the real reason is logged here and nowhere else (AB#4763).
    ///     Rejections are logged, not audited: an audit event per rejected attempt would be an
    ///     unbounded write amplifier into the system database, since nothing rate-limits the callers
    ///     and runtime events are retained indefinitely.
    /// </remarks>
    private async Task EnsureTenantNamespaceAvailableAsync(IOctoAdminSession adminSession,
        string normalizedTenantId, string normalizedDatabaseName, TenantNamespaceMode mode, Guid correlationId)
    {
        ArgumentValidation.ValidateString(nameof(normalizedTenantId), normalizedTenantId);
        ArgumentValidation.ValidateString(nameof(normalizedDatabaseName), normalizedDatabaseName);

        ValidateTenantIdFormat(normalizedTenantId);
        ValidateDatabaseNameFormat(normalizedDatabaseName);

        var systemTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();
        var systemDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);

        // The system tenant has no RtTenant self-record anywhere, so the registry lookup below cannot
        // reserve it - the reservation has to come from the configuration.
        if (normalizedTenantId == systemTenantId)
        {
            throw RejectTenantId(normalizedTenantId, normalizedDatabaseName, correlationId,
                "the tenant id is reserved for the system tenant");
        }

        if (normalizedDatabaseName == systemDatabaseName)
        {
            throw RejectDatabaseName(normalizedTenantId, normalizedDatabaseName, correlationId,
                "the database name is reserved for the system tenant");
        }

        // MongoDB's own databases are never tenant databases. The create path already refuses them
        // because they exist, but attach adopts any existing database — without this an operator
        // could bind 'admin' or 'config' as a tenant, and the next delete would drop it.
        if (ReservedDatabaseNames.Contains(normalizedDatabaseName))
        {
            throw RejectDatabaseName(normalizedTenantId, normalizedDatabaseName, correlationId,
                "the database name is reserved by MongoDB itself");
        }

        var existingById = await GetRtSystemTenantAsync(adminSession, normalizedTenantId);
        if (existingById != null)
        {
            throw RejectTenantId(normalizedTenantId, normalizedDatabaseName, correlationId,
                $"the tenant id is already registered in the system tenant registry (parent " +
                $"'{existingById.ParentTenantId}', database '{existingById.DatabaseName}')");
        }

        var existingByDatabase = await GetRtSystemTenantByDatabaseNameAsync(adminSession, normalizedDatabaseName);
        if (existingByDatabase != null)
        {
            throw RejectDatabaseName(normalizedTenantId, normalizedDatabaseName, correlationId,
                $"the database is already registered to tenant '{existingByDatabase.TenantId}' (parent " +
                $"'{existingByDatabase.ParentTenantId}')");
        }

        var databaseExists = await IsDatabaseExistingAsync(normalizedDatabaseName);
        if (mode == TenantNamespaceMode.CreateNewDatabase && databaseExists)
        {
            // Cluster-wide and case-insensitive, so this also covers databases of other subtrees and
            // non-tenant databases. All of them collapse into the same generic message.
            throw RejectDatabaseName(normalizedTenantId, normalizedDatabaseName, correlationId,
                "a database with this name already exists in the cluster (another tenant's database, " +
                "a non-tenant database, or an orphan of a previous deletion)");
        }

        if (mode == TenantNamespaceMode.AttachExistingDatabase && !databaseExists)
        {
            // Same generic conflict as every other rejection, NOT "the database does not exist".
            // Answering the absent case differently turned attach into a cluster-wide existence
            // oracle: 409 meant taken, 400 meant free, and 204 meant free-and-adoptable, so any
            // caller could confirm a guessed database name anywhere on the platform (AB#4763).
            throw RejectDatabaseName(normalizedTenantId, normalizedDatabaseName, correlationId,
                "no database with this name exists, so there is nothing to attach");
        }

        if (mode == TenantNamespaceMode.AttachExistingDatabase)
        {
            // AB#4945 cross-instance guard: the registry lookups above only cover THIS instance's
            // system database. On a shared MongoDB server another OctoMesh instance (different
            // SystemDatabaseName, Epic AB#4944) may own this database — its ownership marker lives
            // in the tenant database itself, so it is visible here. STRICT by decision: no force
            // override; the owning instance has to detach first (the one sanctioned handover).
            // The rejection stays uniform and reason-free per the AB#4763 rule — the owner goes to
            // the log, never to the caller.
            var ownership = await GetTenantOwnershipStore().GetAsync(normalizedDatabaseName);
            if (ownership != null &&
                !string.Equals(ownership.OwnerSystemDatabaseName, OwnerInstanceIdentity, StringComparison.Ordinal))
            {
                throw RejectDatabaseName(normalizedTenantId, normalizedDatabaseName, correlationId,
                    $"the database is owned by another OctoMesh instance (owner system database " +
                    $"'{ownership.OwnerSystemDatabaseName}', stamped {ownership.AttachedAtUtc:u} for tenant " +
                    $"'{ownership.TenantId}'); detach it in the owning instance first");
            }
        }
    }

    /// <summary>
    ///     The identity of THIS OctoMesh instance for tenant-database ownership (AB#4945): the
    ///     normalized system database name — the one value that is unique per instance on a shared
    ///     MongoDB server (Epic AB#4944 instance separation).
    /// </summary>
    private string OwnerInstanceIdentity => NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);

    private TenantOwnershipStore GetTenantOwnershipStore() =>
        _serviceProvider.GetRequiredService<TenantOwnershipStore>();

    private Exception RejectTenantId(string normalizedTenantId, string normalizedDatabaseName, Guid correlationId,
        string reason)
    {
        _logger.LogWarning(
            "Rejected tenant id '{TenantId}' (requested database '{DatabaseName}', correlation {CorrelationId}) " +
            "because {Reason}. The caller only sees a generic conflict message.",
            normalizedTenantId, normalizedDatabaseName, correlationId, reason);

        return TenantException.TenantIdNotAvailable(normalizedTenantId);
    }

    private Exception RejectDatabaseName(string normalizedTenantId, string normalizedDatabaseName, Guid correlationId,
        string reason)
    {
        _logger.LogWarning(
            "Rejected database name '{DatabaseName}' (requested for tenant '{TenantId}', correlation " +
            "{CorrelationId}) because {Reason}. The caller only sees a generic conflict message.",
            normalizedDatabaseName, normalizedTenantId, correlationId, reason);

        return TenantException.DatabaseNameNotAvailable(normalizedDatabaseName);
    }

    /// <summary>
    ///     Normalizes a database name. Culture-invariant on purpose: a culture-sensitive ToLower can
    ///     map characters differently than the case-insensitive comparison MongoDB does, which would
    ///     let a name pass the availability check and then collide.
    /// </summary>
    protected static string NormalizeDatabaseName(string databaseName)
    {
        return databaseName.Trim().ToLowerInvariant();
    }

    /// <summary>
    ///     Rejects tenant ids that cannot safely round-trip through a URL route segment. Validated up
    ///     front because an invalid id would otherwise fail deep inside the create path - inside the
    ///     try whose catch drops the database (AB#4762).
    /// </summary>
    private static void ValidateTenantIdFormat(string normalizedTenantId)
    {
        if (normalizedTenantId.Length > MaxTenantIdLength ||
            normalizedTenantId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            throw new ArgumentException(
                $"Tenant ID '{normalizedTenantId}' is invalid. Use at most {MaxTenantIdLength} ASCII " +
                "letters, digits, '-' or '_'.", nameof(normalizedTenantId));
        }
    }

    /// <summary>
    ///     Rejects database names MongoDB cannot accept, for the same reason as
    ///     <see cref="ValidateTenantIdFormat" />.
    /// </summary>
    private static void ValidateDatabaseNameFormat(string normalizedDatabaseName)
    {
        // MongoDB measures its limit in BYTES, so a name of multi-byte characters can pass a character
        // count and still be rejected at the first write - inside the create path, i.e. on exactly the
        // code path AB#4762 was about.
        var byteLength = System.Text.Encoding.UTF8.GetByteCount(normalizedDatabaseName);

        if (byteLength > MaxDatabaseNameLength ||
            normalizedDatabaseName.Any(c => InvalidDatabaseNameCharacters.Contains(c) || char.IsControl(c)))
        {
            throw new ArgumentException(
                $"Database name '{normalizedDatabaseName}' is invalid. Use at most {MaxDatabaseNameLength} " +
                $"bytes and none of {new string(InvalidDatabaseNameCharacters)}.",
                nameof(normalizedDatabaseName));
        }
    }

    public async Task<IResultSet<OctoTenant>> GetChildTenantsAsync(IOctoAdminSession adminSession, int? skip = null,
        int? take = null)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var result =
            await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, RtEntityQueryOptions.Create(), skip,
                take);
        return new ResultSet<OctoTenant>(result.Items.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            result.TotalCount, null, null);
    }

    public async Task<OctoTenant> GetChildTenantAsync(IOctoAdminSession adminSession, string tenantId)
    {
        var octoTenant = await TryGetChildTenantAsync(adminSession, tenantId);
        if (octoTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        return octoTenant;
    }

    public async Task<OctoTenant?> TryGetChildTenantAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var normalizedTenantId = tenantId.NormalizeString();

        var rtSystemTenant = await GetRtTenantAsync(adminSession, normalizedTenantId);
        if (rtSystemTenant == null)
        {
            return null;
        }

        return new OctoTenant(rtSystemTenant.TenantId, rtSystemTenant.DatabaseName);
    }

    #endregion Tenant management

    #region Access management

    public async Task<ITenantContext> GetChildTenantContextAsync(string tenantId)
    {
        var tenantContext = await TryGetChildTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        return tenantContext;
    }

    public async Task<ITenantContext?> TryGetChildTenantContextAsync(string tenantId)
    {
        using var systemSession = await GetAdminSessionAsync();
        systemSession.StartTransaction();

        var context = await TryGetChildTenantContextAsync(systemSession, tenantId);
        if (context == null)
        {
            await systemSession.AbortTransactionAsync();
            return null;
        }

        await systemSession.CommitTransactionAsync();

        return context;
    }

    public async Task<ITenantContext> GetChildTenantContextAsync(IOctoAdminSession adminSession, string tenantId)
    {
        var tenantContext = await TryGetChildTenantContextAsync(adminSession, tenantId);
        if (tenantContext == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        return tenantContext;
    }

    public async Task<ITenantContext?> TryGetChildTenantContextAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenant = await TryGetChildTenantAsync(adminSession, tenantId);
        if (tenant == null)
        {
            return null;
        }

        var context = new TenantContext(_loggerFactory, _systemConfiguration, _serviceProvider, tenantId,
            tenant.DatabaseName);

        await UpdateSystemCkModelAsync(tenant.DatabaseName, tenant.TenantId);
        await context.EnsureStreamDataCkModelIfEnabledAsync();
        await context.EnsureServiceManagedCkModelsImportedAsync();

        // AB#4945 lazy ownership claim: tenants attached/created before the ownership marker
        // shipped get stamped on their next resolve, so the existing fleet becomes owned without a
        // migration script. Insert-if-absent — a pre-guard double attachment across two instances
        // is won by the first writer and never flaps.
        await StampOwnershipIfAbsentBestEffortAsync(tenant.TenantId, tenant.DatabaseName);

        return context;
    }

    /// <summary>
    ///     Best-effort, once-per-process-per-tenant lazy stamp of the ownership marker (AB#4945).
    ///     Mirrors the auto-import guard pattern: the guard entry is removed again on failure so a
    ///     transient error retries on the next resolve, and
    ///     <see cref="ClearTenantResolveImportGuards"/> clears it on tenant delete/update.
    /// </summary>
    private async Task StampOwnershipIfAbsentBestEffortAsync(string tenantId, string databaseName)
    {
        var key = tenantId.NormalizeString();
        if (!_ownershipStampAttempted.TryAdd(key, true))
        {
            return;
        }

        try
        {
            await GetTenantOwnershipStore().StampIfAbsentAsync(NormalizeDatabaseName(databaseName), key,
                OwnerInstanceIdentity);
        }
        catch (Exception ex)
        {
            _ownershipStampAttempted.TryRemove(key, out _);
            _logger.LogDebug(ex,
                "Could not lazily stamp the ownership marker for tenant '{TenantId}' (database '{DatabaseName}'); " +
                "will retry on a later resolve", tenantId, databaseName);
        }
    }

    public ITenantRepository GetSystemTenantRepository()
    {
        var normalizedDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();

        var result = GetTenantRepository(normalizedTenantId, normalizedDatabaseName);
        return result;
    }

    public ITenantRepository GetSystemTenantRepositoryAsAdmin()
    {
        var normalizedDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();

        var result = GetTenantRepositoryAsAdmin(normalizedTenantId, normalizedDatabaseName);
        return result;
    }


    public ITenantRepository GetTenantRepository()
    {
        var result = GetTenantRepository(TenantId, DatabaseName);
        return result;
    }

    public ITenantRepository GetTenantRepositoryAsAdmin()
    {
        var result = GetTenantRepositoryAsAdmin(TenantId, DatabaseName);
        return result;
    }

    private IStreamDataRepository? _streamDataRepository;
    private bool _streamDataRepositoryResolved;

    /// <inheritdoc />
    public async Task EnableStreamDataAsync()
    {
        // Concept §5 instance-level gate: tenants can only opt in if the deployment has
        // StreamData:Enabled = true. Without the gate, EnableStreamDataAsync would silently
        // proceed even on instances that haven't been configured for the CrateDB stack at all.
        var instanceConfig = _serviceProvider.GetService<IOptions<StreamDataInstanceConfiguration>>();
        if (instanceConfig?.Value.Enabled != true)
        {
            throw new StreamDataNotEnabledException(
                $"Cannot enable stream data for tenant '{TenantId}': StreamData is disabled at the instance level (set 'StreamData:Enabled' to true in appsettings).");
        }

        _logger.LogInformation("Enabling stream data for tenant '{TenantId}'", TenantId);

        using var session = await GetAdminSessionAsync();
        session.StartTransaction();
        try
        {
            await SetConfigurationAsync(session,
                StreamDataConfigurationKeys.StreamDataEnabledKey,
                StreamDataGlobalSettings.Enabled);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }

        // Ensure the CrateDB table exists
        var repository = GetStreamDataRepository();
        if (repository != null)
        {
            await repository.EnsureDatabaseCreatedAsync();
        }
        else
        {
            _logger.LogWarning(
                "Stream data repository not available in DI. Table creation skipped for tenant '{TenantId}'. " +
                "Ensure AddCrateDbStreamDataRepository() was called during startup.",
                TenantId);
        }

        // Concept §5: the StreamData CK model is loaded only on tenants that opt into stream
        // data. Importing it here keeps the model lifecycle aligned with the feature flag —
        // disabling stream data later does NOT remove the model (entities and history are
        // preserved), but enabling brings everything required for archive management UI to work.
        await EnsureStreamDataCkModelImportedAsync();
    }

    /// <summary>
    /// Imports the StreamData CK model into the tenant. Idempotent — ImportCkModelAsync detects
    /// a model already present and short-circuits. Caller must verify the tenant flag first;
    /// the public entry point is <see cref="EnsureStreamDataCkModelIfEnabledAsync"/>.
    /// </summary>
    /// <remarks>
    /// The exact version comes from the registered <see cref="IStreamDataCkModelDescriptor"/> so
    /// a deploy that ships a newer model version (e.g. 1.0.0 → 1.1.0 adding CkRollupArchive)
    /// auto-upgrades previously-enabled tenants on the next tenant-resolve, no manual
    /// EnableStreamData call needed. When no descriptor is registered we fall back to the
    /// minimum version 1.0.0 to preserve the previous behaviour.
    ///
    /// Includes a downgrade guard: if the tenant already has a higher version installed than
    /// the descriptor's target, the import is skipped. Without this guard, a service that ships
    /// an older descriptor (or the bare 1.0.0 fallback) would silently overwrite the higher
    /// version — ImportCkModelAsync.DeletePreviousVersion strips every previous record of the
    /// model name before inserting, and IsExistingAsync only matches on exact version so it
    /// can't catch the downgrade case.
    /// </remarks>
    private async Task EnsureStreamDataCkModelImportedAsync()
    {
        var descriptor = _serviceProvider.GetService<IStreamDataCkModelDescriptor>();
        var modelId = descriptor?.CkModelId ?? new CkModelId("System.StreamData-1.0.0");
        await ImportEmbeddedCkModelWithDowngradeGuardAsync(modelId);
    }

    /// <summary>
    /// Imports an embedded CK model at its exact <paramref name="modelId"/> version, skipping when a
    /// higher version is already installed (downgrade guard). Shared by the StreamData descriptor path
    /// and the generic <see cref="EnsureServiceManagedCkModelsImportedAsync"/> path. Idempotent —
    /// ImportCkModelAsync short-circuits on an exact-version match.
    /// </summary>
    internal async Task ImportEmbeddedCkModelWithDowngradeGuardAsync(CkModelId modelId)
    {
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName, TenantId);
        var tenantDatabaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, repositoryDataSource, TenantId);
        var anyVersionRange = new CkModelIdVersionRange(modelId.Name, "0.0.0");
        var installed = await _ckModelRepositoryService.IsExistingAsync(anyVersionRange, tenantDatabaseSourceIdentifier);
        if (installed.Exists && installed.ModelId is { } installedModelId &&
            installedModelId.Version.CompareTo(modelId.Version) > 0)
        {
            _logger.LogInformation(
                "Skipping CK model import for tenant '{TenantId}': installed version '{InstalledVersion}' is newer than the embedded target '{TargetVersion}'; downgrade prevented.",
                TenantId, installedModelId, modelId);
            return;
        }

        var operationResult = new OperationResult();
        await ImportCkModelAsync(modelId, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            _logger.LogError(
                "Failed to import embedded CK model '{CkModelId}' into tenant '{TenantId}'. Operation result: {Messages}",
                modelId, TenantId,
                string.Join("; ", operationResult.Messages.Select(m => $"{m.MessageNumber}: {m.MessageText}")));
        }
    }

    /// <inheritdoc />
    public Task ImportCkModelWithDowngradeGuardAsync(CkModelId ckModelId) =>
        ImportEmbeddedCkModelWithDowngradeGuardAsync(ckModelId);

    /// <summary>
    /// Per-process guard so the generic service-managed auto-import runs at most once per
    /// (tenant, model) in this process — mirrors <see cref="_streamDataAutoImportAttempted"/> and
    /// breaks the ImportCkModelAsync -> RetryPendingMigrations -> tenant-resolve -> here recursion.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _serviceManagedCkModelsAttempted = new();

    /// <summary>
    /// Per-process guard of the lazy ownership stamp (AB#4945), keyed by normalized tenant id.
    /// Same lifecycle as the auto-import guards: cleared per tenant by
    /// <see cref="ClearTenantResolveImportGuards"/> on delete/update.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _ownershipStampAttempted = new();

    /// <summary>
    /// Test-only: clears the per-process service-managed auto-import guard so a test can re-trigger
    /// <see cref="EnsureServiceManagedCkModelsImportedAsync"/> for a tenant/model already attempted in
    /// this process. Also clears the lazy ownership-stamp guard (AB#4945). Not used by production code.
    /// </summary>
    internal static void ResetServiceManagedCkModelImportGuardForTests()
    {
        _serviceManagedCkModelsAttempted.Clear();
        _ownershipStampAttempted.Clear();
    }

    /// <inheritdoc cref="ISystemContext.InvalidateTenantResolveImportGuards" />
    public void InvalidateTenantResolveImportGuards(string tenantId) =>
        ClearTenantResolveImportGuards(tenantId);

    /// <inheritdoc cref="ISystemContext.InvalidateTenantRepositoryClientsAsync" />
    public async Task InvalidateTenantRepositoryClientsAsync(string tenantId, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var normalizedTenantId = tenantId.NormalizeString();

        if (string.IsNullOrEmpty(databaseName))
        {
            // Resolve from the tenant record while it still exists (delete publishes its pre-notification
            // before removing the record). Once it is gone the caller must supply the name.
            try
            {
                using var session = await GetAdminSessionAsync().ConfigureAwait(false);
                var tenant = await TryGetChildTenantAsync(session, normalizedTenantId).ConfigureAwait(false);
                databaseName = tenant?.DatabaseName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not resolve the database name of tenant '{TenantId}' to invalidate its cached " +
                    "repository clients.", normalizedTenantId);
                return;
            }
        }

        if (string.IsNullOrEmpty(databaseName))
        {
            _logger.LogDebug(
                "No database name known for tenant '{TenantId}'; nothing to invalidate.", normalizedTenantId);
            return;
        }

        _logger.LogInformation(
            "Dropping cached repository clients of tenant '{TenantId}' (database '{DatabaseName}')",
            normalizedTenantId, databaseName);

        _serviceProvider.GetRequiredService<IAdminRepositoryAccess>().Invalidate(databaseName);
        _serviceProvider.GetRequiredService<IUserRepositoryAccess>().Invalidate(databaseName);
    }

    /// <summary>
    /// Removes the per-process tenant-resolve auto-import guards (<see cref="_serviceManagedCkModelsAttempted"/>
    /// and <see cref="_streamDataAutoImportAttempted"/>) for a single tenant, so the next resolve of that
    /// tenant re-runs <see cref="EnsureServiceManagedCkModelsImportedAsync"/> /
    /// <see cref="EnsureStreamDataCkModelIfEnabledAsync"/>.
    /// </summary>
    /// <remarks>
    /// Called from the Pre-update / Pre-delete tenant lifecycle events (see the
    /// <c>PreUpdatePreDeleteTenantConsumer</c>). Without this, a delete+recreate of a tenant within the
    /// same process lifetime hits the still-set guard and skips the auto-import, leaving the fresh tenant
    /// without its service-managed CK models (e.g. <c>System.UI</c>) — the AB#4294 regression that broke a
    /// re-initialised tenant. The tenant segment is matched case-insensitively so a differently-cased
    /// lifecycle-event tenant id still clears the guard that was set from the resolved context's id.
    /// </remarks>
    private static void ClearTenantResolveImportGuards(string tenantId)
    {
        foreach (var key in _serviceManagedCkModelsAttempted.Keys)
        {
            // Keys are "{TenantId}:{modelName}" — compare only the tenant segment.
            var separatorIndex = key.IndexOf(':');
            if (separatorIndex > 0 &&
                key.AsSpan(0, separatorIndex).Equals(tenantId, StringComparison.OrdinalIgnoreCase))
            {
                _serviceManagedCkModelsAttempted.TryRemove(key, out _);
            }
        }

        foreach (var key in _streamDataAutoImportAttempted.Keys)
        {
            if (key.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
            {
                _streamDataAutoImportAttempted.TryRemove(key, out _);
            }
        }

        foreach (var key in _ownershipStampAttempted.Keys)
        {
            if (key.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
            {
                _ownershipStampAttempted.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Auto-imports every host-registered <see cref="IServiceManagedCkModelDescriptor"/> at its
    /// embedded version (with downgrade guard). Runs on tenant-resolve; no-op when no descriptors are
    /// registered (hosts that don't ship a service-managed model). Unlike the StreamData path this is
    /// not feature-gated — DI registration is the opt-in.
    /// </summary>
    internal async Task EnsureServiceManagedCkModelsImportedAsync()
    {
        foreach (var descriptor in _serviceProvider.GetServices<IServiceManagedCkModelDescriptor>())
        {
            var modelId = descriptor.CkModelId;
            if (!_serviceManagedCkModelsAttempted.TryAdd($"{TenantId}:{modelId.Name}", true)) continue;
            await ImportEmbeddedCkModelWithDowngradeGuardAsync(modelId);
        }
    }

    /// <summary>
    /// Per-process guard that prevents the auto-import hook from re-entering during the
    /// tenant-resolve recursion triggered by ImportCkModelAsync's pending-migrations check.
    /// Once a tenant has been auto-reconciled in this process, subsequent resolves skip the
    /// hook (manual EnableStreamDataAsync calls bypass this guard).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _streamDataAutoImportAttempted = new();

    /// <summary>
    /// Idempotent reconciliation hook for the tenant-resolve path: when a tenant has the
    /// StreamData feature flag set to <c>Enabled</c> AND the deployment instance flag is on,
    /// make sure the StreamData CK model is in its catalog. Lets a deploy that newly introduces
    /// the model auto-promote it to previously-enabled tenants without requiring an explicit
    /// EnableStreamData call (concept §5). No-op when either flag is off, or when the tenant
    /// has already been reconciled in this process.
    /// </summary>
    internal async Task EnsureStreamDataCkModelIfEnabledAsync()
    {
        // First-pass guard avoids the recursion through ImportCkModelAsync ->
        // RetryPendingMigrationsAsync -> CkModelUpgradeService -> tenant resolve -> here.
        if (!_streamDataAutoImportAttempted.TryAdd(TenantId, true)) return;

        var instanceConfig = _serviceProvider.GetService<IOptions<StreamDataInstanceConfiguration>>();
        if (instanceConfig?.Value.Enabled != true) return;

        try
        {
            if (!await IsStreamDataEnabledAsync()) return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read stream-data tenant flag for '{TenantId}'; skipping StreamData CK model auto-import.",
                TenantId);
            return;
        }

        await EnsureStreamDataCkModelImportedAsync();
    }

    /// <inheritdoc />
    public async Task DisableStreamDataAsync()
    {
        // AB#4255: disabling is a verified precondition, not a teardown. An Activated archive still
        // accepts ingest and is still ticked by the rollup/recompute orchestrators (they gate on the
        // archive status, not on this flag), so the flag is only switched off once nothing is live.
        // Disabled/Failed/Created archives keep their entities and tables; the tenant drop removes
        // the tables with the database.
        var activated = await GetActivatedArchivesAsync();
        if (activated.Count > 0)
        {
            _logger.LogWarning(
                "Refused to disable stream data for tenant '{TenantId}': {Count} archive(s) still activated",
                TenantId, activated.Count);
            throw StreamDataDisableBlockedException.Create(TenantId, activated);
        }

        _logger.LogInformation("Disabling stream data for tenant '{TenantId}'", TenantId);

        using var session = await GetAdminSessionAsync();
        session.StartTransaction();
        try
        {
            await SetConfigurationAsync(session,
                StreamDataConfigurationKeys.StreamDataEnabledKey,
                StreamDataGlobalSettings.Disabled);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    /// <summary>
    /// Every non-soft-deleted archive of the tenant whose status is <see cref="CkArchiveStatus.Activated"/>,
    /// or an empty list when the tenant has not imported the System.StreamData model (without the
    /// model no archive entity can exist, and enumerating the store would throw CkCacheException).
    /// Read failures propagate: an unreadable state must never read as "nothing is activated".
    /// </summary>
    private async Task<IReadOnlyList<ArchiveSnapshot>> GetActivatedArchivesAsync()
    {
        // Same tenant-level gate as GetRollupOrchestrator: the store's EnumerateAsync resolves the
        // RtArchive CK type from the cache and throws when the model was never imported.
        if (!_cacheService.TryGetRtCkType(TenantId, ArchiveRtCkTypeId, out _))
        {
            return [];
        }

        var activated = new List<ArchiveSnapshot>();
        await foreach (var archive in GetArchiveRuntimeStore().EnumerateAsync())
        {
            if (archive.Status == CkArchiveStatus.Activated)
            {
                activated.Add(archive);
            }
        }

        return activated;
    }

    /// <inheritdoc />
    public async Task<bool> IsStreamDataEnabledAsync()
    {
        using var session = await GetAdminSessionAsync();
        session.StartTransaction();
        try
        {
            var settings = await GetConfigurationAsync<StreamDataGlobalSettings>(
                session, StreamDataConfigurationKeys.StreamDataEnabledKey, null);
            await session.CommitTransactionAsync();
            return settings is { IsEnabled: true };
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public IStreamDataRepository? GetStreamDataRepository()
    {
        if (_streamDataRepositoryResolved)
        {
            return _streamDataRepository;
        }

        // Resolve the stream data factory from DI. If not registered
        // (caller didn't call AddCrateDbStreamDataRepository), return null.
        var factory = _serviceProvider.GetService<IStreamDataRepositoryFactory>();
        if (factory == null)
        {
            _streamDataRepositoryResolved = true;
            return null;
        }

        // Wire the per-tenant archive store into the repository so the per-archive status guard
        // (T14) and column-list lookup at insert time (T17) work. Both are no-ops when the
        // factory was registered without StreamData enabled, but with a tenant context in scope
        // the store is always available — we own its lifetime here. The rollup store is optional
        // and only needed for cascade-rollup chain-aware aggregation resolution; null when the
        // tenant doesn't have rollup support registered.
        _streamDataRepository = factory.Create(
            TenantId, GetArchiveRuntimeStore(), GetRollupArchiveRuntimeStore(), GetArchiveRecomputeStateStore());
        _streamDataRepositoryResolved = true;
        return _streamDataRepository;
    }

    private IArchiveRuntimeStore? _ckArchiveRuntimeStore;

    /// <inheritdoc />
    public IArchiveRuntimeStore GetArchiveRuntimeStore()
    {
        return _ckArchiveRuntimeStore ??= new MongoArchiveRuntimeStore(GetTenantRepository());
    }

    private IArchiveLifecycleService? _archiveLifecycleService;
    private bool _archiveLifecycleServiceResolved;

    /// <inheritdoc />
    public IArchiveLifecycleService? GetArchiveLifecycleService()
    {
        if (_archiveLifecycleServiceResolved)
        {
            return _archiveLifecycleService;
        }

        var streamData = GetStreamDataRepository();
        if (streamData is null)
        {
            _archiveLifecycleServiceResolved = true;
            return null;
        }

        var audit = _serviceProvider.GetService<IArchiveAuditTrail>()
                    ?? new LoggingArchiveAuditTrail(_loggerFactory.CreateLogger<LoggingArchiveAuditTrail>());
        // Pass the (optional) rollup store so DeleteAsync's source-in-use guard is active when
        // rollup support is wired in this deployment. Null = guard self-disables.
        _archiveLifecycleService = new ArchiveLifecycleService(
            TenantId,
            GetArchiveRuntimeStore(),
            streamData,
            audit,
            _loggerFactory.CreateLogger<ArchiveLifecycleService>(),
            GetRollupArchiveRuntimeStore(),
            // AB#4300: wire the recompute stores so disable/delete purges any queued recompute work
            // (pending ranges + the active job) instead of leaving an un-processable Pending ghost.
            GetArchiveRecomputeStateStore(),
            GetRecomputeJobStore());
        _archiveLifecycleServiceResolved = true;
        return _archiveLifecycleService;
    }

    private IRollupArchiveRuntimeStore? _rollupStore;

    /// <inheritdoc />
    public IRollupArchiveRuntimeStore? GetRollupArchiveRuntimeStore()
    {
        return _rollupStore ??= new MongoRollupArchiveRuntimeStore(GetTenantRepository());
    }

    private ITimeRangeArchiveRuntimeStore? _timeRangeStore;

    /// <inheritdoc />
    public ITimeRangeArchiveRuntimeStore? GetTimeRangeArchiveRuntimeStore()
    {
        return _timeRangeStore ??= new MongoTimeRangeArchiveRuntimeStore(GetTenantRepository());
    }

    private IArchiveRecomputeStateStore? _archiveRecomputeStateStore;

    /// <inheritdoc />
    public IArchiveRecomputeStateStore GetArchiveRecomputeStateStore()
    {
        return _archiveRecomputeStateStore ??= new MongoArchiveRecomputeStateStore(GetTenantRepository());
    }

    private IRecomputeJobStore? _recomputeJobStore;

    /// <inheritdoc />
    public IRecomputeJobStore GetRecomputeJobStore()
    {
        return _recomputeJobStore ??= new MongoRecomputeJobStore(GetTenantRepository());
    }

    private IRollupArchiveLifecycleService? _rollupLifecycleService;
    private bool _rollupLifecycleServiceResolved;

    /// <inheritdoc />
    public IRollupArchiveLifecycleService? GetRollupArchiveLifecycleService()
    {
        if (_rollupLifecycleServiceResolved)
        {
            return _rollupLifecycleService;
        }

        var rollupStore = GetRollupArchiveRuntimeStore();
        if (rollupStore is null)
        {
            _rollupLifecycleServiceResolved = true;
            return null;
        }

        // The create path resolves the source archive's TargetCkTypeId through the shared
        // CkArchive store. Always available — GetArchiveRuntimeStore is non-nullable.
        var archiveStore = GetArchiveRuntimeStore();

        var audit = _serviceProvider.GetService<IArchiveAuditTrail>()
                    ?? new LoggingArchiveAuditTrail(_loggerFactory.CreateLogger<LoggingArchiveAuditTrail>());
        _rollupLifecycleService = new RollupArchiveLifecycleService(
            TenantId,
            rollupStore,
            archiveStore,
            audit,
            _loggerFactory.CreateLogger<RollupArchiveLifecycleService>(),
            // AB#4184, Phase 6: the stream-data repository lets RewindWatermarkAsync clear recompute
            // generation pointers over the rewound range. Null when stream data is not enabled.
            GetStreamDataRepository());
        _rollupLifecycleServiceResolved = true;
        return _rollupLifecycleService;
    }

    private IRollupOrchestrator? _rollupOrchestrator;

    private static readonly RtCkId<CkTypeId> RollupArchiveRtCkTypeId =
        new("System.StreamData", "RollupArchive");

    /// <summary>
    /// The abstract archive base type; present exactly when the System.StreamData model is imported.
    /// Used as the tenant-level gate before enumerating archives (AB#4255).
    /// </summary>
    private static readonly RtCkId<CkTypeId> ArchiveRtCkTypeId =
        new("System.StreamData", "Archive");

    /// <inheritdoc />
    public IRollupOrchestrator? GetRollupOrchestrator()
    {
        if (_rollupOrchestrator != null)
        {
            return _rollupOrchestrator;
        }

        var streamData = GetStreamDataRepository();
        var rollupStore = GetRollupArchiveRuntimeStore();
        if (streamData is null || rollupStore is null)
        {
            // Service-level wiring is missing — orchestrator stays disabled for every tenant.
            return null;
        }

        // Tenant-level gate: the orchestrator's tick calls IRollupArchiveRuntimeStore.EnumerateAsync,
        // which delegates to GetRtEntitiesByTypeAsync<RtRollupArchive>(). That lookup throws
        // CkCacheException if the tenant has not imported the System.StreamData CK model
        // (i.e. the tenant has not opted into stream data). The cache check has to run again on
        // every call rather than being memoised because EnsureStreamDataCkModelIfEnabledAsync can
        // auto-import the model later in this same process — and at that point the next tick
        // should wire the orchestrator without requiring a restart.
        if (!_cacheService.TryGetRtCkType(TenantId, RollupArchiveRtCkTypeId, out _))
        {
            return null;
        }

        var audit = _serviceProvider.GetService<IArchiveAuditTrail>()
                    ?? new LoggingArchiveAuditTrail(_loggerFactory.CreateLogger<LoggingArchiveAuditTrail>());
        // AB#4306: opt-in provisional refresh of each rollup's current open bucket. Read from the
        // same RollupOrchestratorOptions the hosted service binds (StreamData:Rollup); default off.
        var refreshOpenBucket =
            _serviceProvider.GetService<IOptions<RollupOrchestratorOptions>>()?.Value.RefreshOpenBucket ?? false;
        _rollupOrchestrator = new RollupOrchestrator(
            TenantId,
            GetArchiveRuntimeStore(),
            rollupStore,
            streamData,
            audit,
            _loggerFactory.CreateLogger<RollupOrchestrator>(),
            refreshOpenBucket: refreshOpenBucket);
        return _rollupOrchestrator;
    }

    private IRecomputeOrchestrator? _recomputeOrchestrator;

    /// <inheritdoc />
    public IRecomputeOrchestrator? GetRecomputeOrchestrator()
    {
        if (_recomputeOrchestrator != null)
        {
            return _recomputeOrchestrator;
        }

        var streamData = GetStreamDataRepository();
        var rollupStore = GetRollupArchiveRuntimeStore();
        if (streamData is null || rollupStore is null)
        {
            return null;
        }

        // Same tenant-level gate as GetRollupOrchestrator: skip until the tenant has imported the
        // System.StreamData CK model (otherwise the stores' EnumerateAsync throws CkCacheException).
        if (!_cacheService.TryGetRtCkType(TenantId, RollupArchiveRtCkTypeId, out _))
        {
            return null;
        }

        // The CrateDB stream-data repository doubles as the recompute executor (it owns the wired
        // CrateDB clients). If a non-Crate repository is ever plugged in, recompute stays disabled.
        if (streamData is not IArchiveRecomputeExecutor executor)
        {
            return null;
        }

        var audit = _serviceProvider.GetService<IArchiveAuditTrail>()
                    ?? new LoggingArchiveAuditTrail(_loggerFactory.CreateLogger<LoggingArchiveAuditTrail>());

        // AB#4283: a decade-long recompute/backfill is split into bucket-aligned chunks so no single
        // CrateDB statement exceeds the per-statement timeout. Chunk size is overridable via
        // OCTO_StreamData__RecomputeMaxBucketsPerChunk for tuning against a specific cluster; the
        // default is sized to stay well inside the 30s statement budget.
        var maxBucketsPerChunk = ResolveRecomputeMaxBucketsPerChunk();

        _recomputeOrchestrator = new RecomputeOrchestrator(
            TenantId,
            GetArchiveRuntimeStore(),
            rollupStore,
            new RollupDependencyGraph(rollupStore),
            GetArchiveRecomputeStateStore(),
            GetRecomputeJobStore(),
            executor,
            // AB#4269: backfill resolves the source archive's earliest timestamp via the stream-data
            // repository (the same CrateDB instance the executor wraps), then recomputes [min, now).
            streamData,
            audit,
            _loggerFactory.CreateLogger<RecomputeOrchestrator>(),
            () => DateTime.UtcNow,
            maxBucketsPerChunk);
        return _recomputeOrchestrator;
    }

    /// <summary>
    /// Resolves the recompute chunk size (AB#4283). Honours the
    /// <c>OCTO_StreamData__RecomputeMaxBucketsPerChunk</c> environment override when it parses to a
    /// positive integer; otherwise falls back to <see cref="RecomputeOrchestrator.DefaultMaxBucketsPerChunk"/>.
    /// </summary>
    private int ResolveRecomputeMaxBucketsPerChunk()
    {
        var raw = Environment.GetEnvironmentVariable("OCTO_StreamData__RecomputeMaxBucketsPerChunk");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return RecomputeOrchestrator.DefaultMaxBucketsPerChunk;
    }

    #endregion Access management

    #region Configuration

    public async Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoAdminSession adminSession, string key,
        TValueType? defaultValue) where
        TValueType
        : class
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        var o = await GetConfigAsync(adminSession, key, defaultValue);

        return o;
    }

    public async Task<string?> GetConfigurationAsync(IOctoAdminSession adminSession, string key,
        string? defaultValue = null)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        return await GetConfigAsync(adminSession, key, defaultValue);
    }


    public async Task SetConfigurationAsync<TValueType>(IOctoAdminSession adminSession, string key, TValueType value)
        where TValueType : struct

    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigurationAsync(adminSession, key, (object)value);
    }

    public async Task SetConfigurationAsync(IOctoAdminSession adminSession, string key, string value)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigurationAsync(adminSession, key, (object)value);
    }

    public async Task SetConfigurationAsync(IOctoAdminSession adminSession, string key, object value)
    {
        ArgumentValidation.ValidateString(nameof(key), key);

        var tenantRepository = GetTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenantConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync<RtTenantConfiguration>(adminSession, queryOptions);
        var configuration = resultSet.Items.FirstOrDefault();
        if (configuration == null)
        {
            configuration = new RtTenantConfiguration { RtWellKnownName = key, ConfigurationValue = value.Serialize() };
            await tenantRepository.InsertOneRtEntityAsync(adminSession, configuration);
        }
        else
        {
            configuration.ConfigurationValue = value.Serialize();
            await tenantRepository.ReplaceOneRtEntityByIdAsync(adminSession, configuration.RtId, configuration);
        }
    }

    public async Task DeleteConfigurationAsync(IOctoAdminSession adminSession, string key)
    {
        ArgumentValidation.ValidateString(nameof(key), key);

        var tenantRepository = GetTenantRepositoryAsAdmin();

        var fieldFilterCriteria = FieldFilterCriteria
            .Create()
            .FieldEquals(nameof(RtTenantConfiguration.RtWellKnownName), key);

        await tenantRepository.DeleteOneRtEntityAsync<RtTenantConfiguration>(adminSession, fieldFilterCriteria,
            DeleteOptions.Erase);
    }

    #endregion Configuration

    #region Construction Kits

    public async Task ImportCkModelAsync(CkCompiledModelRoot ckCompiledModelRoot)
    {
        Guid correlationId = Guid.NewGuid();

        // Use the admin data source for the import flow: UpdateCollectionsAsync may need to run
        // `collMod` to reconcile the changeStreamPreAndPostImages option on existing collections,
        // which requires the `collMod` action — not granted to the tenant `readWrite` user.
        // This matches the pattern used by UpdateIndexesAsync (schema-level ops run as admin).
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName, TenantId);
        var tenantDatabaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, repositoryDataSource, TenantId);

        // Capture schema versions BEFORE importing (for migration detection)
        var previousSchemaVersions = await GetSchemaVersionsDirectAsync(tenantDatabaseSourceIdentifier);

        // Capture declared display rules BEFORE importing (for backfill sweep detection, AB#4812)
        var displayRulesBeforeImport = await GetDeclaredDisplayRulesDirectAsync(tenantDatabaseSourceIdentifier);

        // If the compiled model contains inline migration data, make it available
        // to the migration content provider so migrations can run without NuGet references
        CompiledModelCkMigrationContentProvider? compiledMigrationProvider = null;
        if (ckCompiledModelRoot.Migrations != null)
        {
            compiledMigrationProvider = _serviceProvider.GetService<CompiledModelCkMigrationContentProvider>();
            compiledMigrationProvider?.SetMigrationData(
                ckCompiledModelRoot.ModelId, ckCompiledModelRoot.Migrations);
        }

        try
        {
            _logger.LogInformation("Importing CK Model '{CkModelId}' into tenant '{TenantId}'",
                ckCompiledModelRoot.ModelId, TenantId);

            await _tenantNotifications.NotifyPreTenantUpdateAsync(TenantId, correlationId);

            try
            {
                await _ckModelRepositoryService.UpdateModelAsync(ckCompiledModelRoot,
                    tenantDatabaseSourceIdentifier);

                _logger.LogInformation("CK Model '{CkModelId}' imported into tenant '{TenantId}'",
                    ckCompiledModelRoot.ModelId, TenantId);

                // Run migrations after successful import
                await RunCkModelMigrationsForImportAsync(TenantId, ckCompiledModelRoot.ModelId,
                    previousSchemaVersions);

                // Invalidate cache so the next access triggers a fresh load with all currently-available models
                if (_cacheService.IsTenantLoaded(TenantId))
                {
                    _cacheService.Unload(TenantId);
                }

                // Enqueue backfill sweeps for types whose display rules changed (AB#4812)
                await EnqueueDisplayRuleSweepsAsync(displayRulesBeforeImport, tenantDatabaseSourceIdentifier);
            }
            catch (ModelValidationException ex)
            {
                // Re-throw for explicit imports (CLI / API). Unlike service startup (the CkModelId
                // overload below), this overload is called by the user and must report failures.
                _logger.LogError(
                    "Import of CK model '{CkModelId}' for tenant '{TenantId}' failed due to missing dependencies: {Message}",
                    ckCompiledModelRoot.ModelId, TenantId, ex.Message);
                throw;
            }

            // Only send the notification after a successful import (not in finally).
            // Sending this on failure would trigger other services to re-process unnecessarily,
            // potentially causing an import loop.
            await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
        }
        finally
        {
            compiledMigrationProvider?.ClearMigrationData(ckCompiledModelRoot.ModelId);
        }
    }

    public async Task ImportCkModelAsync(CkModelId ckModelId, OperationResult operationResult)
    {
        Guid correlationId = Guid.NewGuid();

        // Use the admin data source for the import flow: UpdateCollectionsAsync may need to run
        // `collMod` to reconcile the changeStreamPreAndPostImages option on existing collections,
        // which requires the `collMod` action — not granted to the tenant `readWrite` user.
        // This matches the pattern used by UpdateIndexesAsync (schema-level ops run as admin).
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName, TenantId);
        var tenantDatabaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, repositoryDataSource, TenantId);
        if (await _ckModelRepositoryService.IsExistingAsync(ckModelId, tenantDatabaseSourceIdentifier))
        {
            _logger.LogDebug("CK Model '{CkModelId}' already exists in tenant '{TenantId}', skipping import",
                ckModelId, TenantId);

            // Even though the model is already imported, check for pending migrations.
            // A previous migration attempt may have failed, leaving the MigrationHistory
            // at an older version while the CkModel schema is already at the target version.
            await RetryPendingMigrationsAsync(ckModelId);
            return;
        }

        // Capture schema versions BEFORE importing (for migration detection)
        var previousSchemaVersions = await GetSchemaVersionsDirectAsync(tenantDatabaseSourceIdentifier);

        // Capture declared display rules BEFORE importing (for backfill sweep detection, AB#4812)
        var displayRulesBeforeImport = await GetDeclaredDisplayRulesDirectAsync(tenantDatabaseSourceIdentifier);

        _logger.LogInformation("Importing CK Model '{CkModelId}' into tenant '{TenantId}'", ckModelId, TenantId);

        await _tenantNotifications.NotifyPreTenantUpdateAsync(TenantId, correlationId);

        var ckCompiledModelRoot =
            await _catalogService.GetAsync(ckModelId, operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw TenantException.ErrorDuringSystemModelLoad(operationResult);
        }

        if (ckCompiledModelRoot == null)
        {
            throw TenantException.ModelNotFoundInACatalog(ckModelId);
        }

        try
        {
            await _ckModelRepositoryService.UpdateModelAsync(ckCompiledModelRoot, tenantDatabaseSourceIdentifier);

            _logger.LogInformation("CK Model '{CkModelId}' imported into tenant '{TenantId}'", ckModelId, TenantId);

            // Run migrations after successful import
            await RunCkModelMigrationsForImportAsync(TenantId, ckModelId, previousSchemaVersions);

            // Invalidate cache so the next access triggers a fresh load with all currently-available models
            if (_cacheService.IsTenantLoaded(TenantId))
            {
                _cacheService.Unload(TenantId);
            }

            // Enqueue backfill sweeps for types whose display rules changed (AB#4812)
            await EnqueueDisplayRuleSweepsAsync(displayRulesBeforeImport, tenantDatabaseSourceIdentifier);
        }
        catch (ModelValidationException ex)
        {
            // Gracefully handle missing dependencies - this can happen when services start
            // in parallel and a dependent CK model is still being imported by another service.
            // A RabbitMQ tenant update notification will be sent when the dependency is ready,
            // allowing this import to succeed on the next attempt.
            _logger.LogWarning(
                "Skipping CK model '{CkModelId}' import for tenant '{TenantId}' due to missing dependencies: {Message}. " +
                "This import will be retried when the dependent CK model becomes available.",
                ckModelId, TenantId, ex.Message);
            // Don't add to operationResult as error - this is a transient condition that will resolve itself
        }

        // Only send the notification after a successful import (not in finally).
        // Sending this on failure would trigger other services to re-process unnecessarily,
        // potentially causing an import loop.
        await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
    }

    public async Task<bool> IsCkModelExistingAsync(CkModelId ckModelId)
    {
        var repositoryDataSource = CreateRepositoryDataSource(DatabaseName);

        var r = await _ckModelRepositoryService.IsExistingAsync(
            ckModelId.ToVersionRange(),
            new TenantDatabaseSourceIdentifier(null, repositoryDataSource, TenantId));
        return r.Exists;
    }

    public async Task CustomizeCkEnumAsync(CkId<CkEnumId> ckEnumId, ICollection<CkEnumUpdate> ckEnumUpdates,
        CancellationToken? cancellationToken = null)
    {
        Guid correlationId = Guid.NewGuid();

        try
        {
            var repositoryDataSource = CreateRepositoryDataSource(DatabaseName);

            await _tenantNotifications.NotifyPreTenantUpdateAsync(TenantId, correlationId);
            await _ckModelRepositoryService.CustomizeCkEnumAsync(
                ckEnumId,
                ckEnumUpdates, new TenantDatabaseSourceIdentifier(null, repositoryDataSource, TenantId), cancellationToken);
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
        }
    }

    #endregion

    #region Private methods

    private ITenantRepository GetTenantRepository(string tenantId, string databaseName)
    {
        var repositoryDataSource = CreateRepositoryDataSource(databaseName);

        var tenantRepository = new TenantRepository(tenantId, _metricsContext, _cacheService, _modelLoaderService,
            repositoryDataSource,
            _bulkRtMutation,
            _serviceProvider.GetService<IDataSecurityFilterFactory>(),
            _serviceProvider.GetService<IDataPermissionResolver>(),
            _serviceProvider.GetService<IAuditEventSink>());
        return tenantRepository;
    }

    private ITenantRepository GetTenantRepositoryAsAdmin(string tenantId, string databaseName)
    {
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(databaseName, tenantId);

        var tenantRepository = new TenantRepository(tenantId, _metricsContext, _cacheService, _modelLoaderService,
            repositoryDataSource,
            _bulkRtMutation,
            _serviceProvider.GetService<IDataSecurityFilterFactory>(),
            _serviceProvider.GetService<IDataPermissionResolver>(),
            _serviceProvider.GetService<IAuditEventSink>());
        return tenantRepository;
    }

    private IMongoDbRepositoryDataSource CreateRepositoryDataSource(string databaseName)
    {
        return new MongoDbRepositoryDataSource(_loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _serviceProvider.GetRequiredService<IUserRepositoryAccess>(), databaseName, TenantId);
    }

    protected IMongoDbRepositoryDataSource CreateRepositoryDataSourceAsAdmin(string databaseName, string tenantId)
    {
        return new MongoDbRepositoryDataSource(_loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _adminRepositoryClient, databaseName, tenantId);
    }

    /// <summary>
    ///     Looks a tenant up in THIS context's own registry, i.e. among the caller's direct children.
    ///     The counterpart <see cref="GetRtSystemTenantAsync" /> searches the platform-wide registry -
    ///     the two must never be swapped (AB#4763).
    /// </summary>
    private async Task<RtTenant?> GetRtTenantAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.NormalizeString());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, queryOptions);
        return FirstAndWarnOnDuplicates(resultSet, $"tenant id '{tenantId.NormalizeString()}'",
            $"the registry of tenant '{TenantId}'");
    }

    /// <summary>
    ///     Looks a tenant up in the PLATFORM-WIDE registry held by the system tenant. This is the
    ///     authority for tenant-id uniqueness; the subtree-local counterpart is
    ///     <see cref="GetRtTenantAsync" />.
    /// </summary>
    /// <remarks>
    ///     This used to be a verbatim copy of <see cref="GetRtTenantAsync" /> and therefore queried the
    ///     current tenant's own database, which made the "global" uniqueness check blind to everything
    ///     outside the caller's own children (AB#4763). Passing this context's admin session to a
    ///     repository on the system database is safe: <see cref="CreateRepositoryDataSourceAsAdmin" />
    ///     reuses this context's admin client and only re-targets the database name, exactly like the
    ///     registry insert and delete already do.
    /// </remarks>
    private async Task<RtTenant?> GetRtSystemTenantAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.NormalizeString());

        var resultSet = await systemTenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, queryOptions);
        return FirstAndWarnOnDuplicates(resultSet, $"tenant id '{tenantId.NormalizeString()}'",
            "the system tenant registry");
    }

    /// <summary>
    ///     Finds the tenant that the platform-wide registry maps to a database name, so a database
    ///     already claimed by another tenant cannot be claimed a second time (AB#4763).
    /// </summary>
    protected async Task<RtTenant?> GetRtSystemTenantByDatabaseNameAsync(IOctoAdminSession adminSession,
        string normalizedDatabaseName)
    {
        var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenant.DatabaseName), FieldFilterOperator.Equals, normalizedDatabaseName);

        var resultSet = await systemTenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, queryOptions);
        return FirstAndWarnOnDuplicates(resultSet, $"database name '{normalizedDatabaseName}'",
            "the system tenant registry");
    }

    /// <summary>
    ///     Returns the first match and warns when the lookup was ambiguous.
    /// </summary>
    /// <remarks>
    ///     Deliberately does NOT impose a sort order. Which row wins decides which database a tenant
    ///     resolves to and which database a delete drops, so ordering rows that were already ambiguous
    ///     could silently move a live tenant onto a different database. Duplicates are typically a
    ///     leftover of AB#4763; the namespace gate no longer lets them through, but nothing at the
    ///     database level enforces uniqueness (the Tenant CK type's TenantId index is NOT unique), so
    ///     two creates racing through the gate in separate transactions can still both commit.
    ///     Surfacing duplicates so an operator can clean them up is the useful behaviour, changing
    ///     resolution underneath a running tenant is not.
    /// </remarks>
    private RtTenant? FirstAndWarnOnDuplicates(IResultSet<RtTenant> resultSet, string lookupDescription,
        string registryDescription)
    {
        var matches = resultSet.Items.ToList();
        if (matches.Count > 1)
        {
            _logger.LogWarning(
                "Ambiguous tenant lookup for {Lookup} in {Registry}: {Count} records match, mapped to databases " +
                "[{DatabaseNames}]. This is corruption (typically an AB#4763 leftover, or two creates that raced " +
                "the namespace gate) and must be cleaned up manually; resolution is left unchanged and picks an " +
                "arbitrary one.",
                lookupDescription, registryDescription, matches.Count,
                string.Join(", ", matches.Select(m => m.DatabaseName)));
        }

        return matches.FirstOrDefault();
    }

    protected async Task<bool> IsDatabaseExistingAsync(string databaseName)
    {
        return await _adminRepositoryClient.IsRepositoryExistingAsync(databaseName);
    }

    private async Task<TType?> GetConfigAsync<TType>(IOctoSession systemSession, string key, TType? defaultValue)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenantConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync<RtTenantConfiguration>(systemSession, queryOptions);
        var configuration = resultSet.Items.FirstOrDefault();
        if (configuration == null || configuration.ConfigurationValue == null)
        {
            return defaultValue;
        }

        var result = configuration.ConfigurationValue.Deserialize<TType>();
        return result;
    }

    #endregion Private methods
}
