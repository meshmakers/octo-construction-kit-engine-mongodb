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
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.CkModelMigrations;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

[DebuggerDisplay("TenantId = {TenantId}")]
public class TenantContext : ITenantContext
{
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

        var normalizedDatabaseName = databaseName.ToLower();
        var normalizedTenantId = tenantId.NormalizeString();
        if (await IsTenantExistingAsync(adminSession, normalizedTenantId))
        {
            throw TenantException.TenantDoesAlreadyExist(tenantId);
        }

        Guid correlationId = Guid.NewGuid();

        try
        {
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(tenantId, correlationId);

            // Create the database
            await CreateTenantInternalAsync(databaseName);

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
        }
        catch (Exception)
        {
            await _adminRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            throw;
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await _tenantNotifications.NotifyPosTenantCreateAsync(tenantId, correlationId);
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
        var databaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, databaseContext);
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

    protected async Task CreateTenantInternalAsync(string databaseName)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);

        var normalizedDatabaseName = databaseName.ToLower();

        if (await IsDatabaseExistingAsync(normalizedDatabaseName))
        {
            throw TenantException.TenantDatabaseDoesAlreadyExist(normalizedDatabaseName);
        }

        await _adminRepositoryClient.CreateRepositoryAsync(normalizedDatabaseName);
        await _adminRepositoryClient.CreateUser(_systemConfiguration.Value.AuthenticationDatabaseName,
            normalizedDatabaseName, string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName),
            _systemConfiguration.Value.DatabaseUserPassword);
    }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task AttachChildTenantAsync(IOctoAdminSession adminSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var normalizedDatabaseName = databaseName.ToLower();
        var normalizedTenantId = tenantId.NormalizeString();

        if (await IsTenantExistingAsync(adminSession, tenantId))
        {
            throw TenantException.TenantDoesAlreadyExist(tenantId);
        }

        if (!await IsDatabaseExistingAsync(databaseName))
        {
            throw TenantException.TenantDatabaseDoesNotExist(databaseName);
        }

        Guid correlationId = Guid.NewGuid();

        try
        {
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(tenantId, correlationId);

            // Add the new tenant as child tenant of the current one
            if (TenantId != _systemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var octoTenant = new RtTenant { TenantId = tenantId, DatabaseName = databaseName };

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
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await _tenantNotifications.NotifyPosTenantCreateAsync(tenantId, correlationId);
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
                FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId),
                    tenantId.NormalizeString()), DeleteOptions.Erase);
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await _tenantNotifications.NotifyPosTenantDeleteAsync(tenantId, correlationId);
        }
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

            await DropChildTenantAsync(adminSession, tenantId);
            await CreateChildTenantAsync(adminSession, octoTenant.DatabaseName, tenantId);
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantUpdateAsync(tenantId, correlationId);
        }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task DropChildTenantAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenantRepository = GetTenantRepositoryAsAdmin();

        var octoTenant = await GetRtTenantAsync(adminSession, tenantId);
        if (octoTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        Guid correlationId = Guid.NewGuid();

        try
        {
            await _tenantNotifications.NotifyPreTenantDeleteAsync(tenantId, correlationId);

            await _adminRepositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);

            // Deletes the tenant entry from the current tenant
            await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId),
                    tenantId.NormalizeString()), DeleteOptions.Erase);

            // If the current tenant is not the system tenant, we need to delete the tenant entry in system tenant too.
            // Add the new tenant as child tenant of the current one
            if (TenantId != _systemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
                await systemTenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                    FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId),
                        tenantId.NormalizeString()), DeleteOptions.Erase);
            }
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantDeleteAsync(tenantId, correlationId);
        }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsChildTenantExistingAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(adminSession, tenantId);
        return octoTenant != null;
    }

    private async Task<bool> IsTenantExistingAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtSystemTenantAsync(adminSession, tenantId);
        return octoTenant != null;
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

        return context;
    }

    public ITenantRepository GetSystemTenantRepository()
    {
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();

        var result = GetTenantRepository(normalizedTenantId, normalizedDatabaseName);
        return result;
    }

    public ITenantRepository GetSystemTenantRepositoryAsAdmin()
    {
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
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

        // Ensure the StreamData CK model is installed at the version the host ships. The host
        // registers an IStreamDataCkModelDescriptor with the CkModelId that was compiled in;
        // ImportCkModelAsync is idempotent (skips when the version already matches) and runs
        // auto-bridged migrations when the host has shipped a newer version.
        var streamDataCkModelDescriptor = _serviceProvider.GetService<IStreamDataCkModelDescriptor>();
        if (streamDataCkModelDescriptor != null)
        {
            var importOperationResult = new OperationResult();
            await ImportCkModelAsync(streamDataCkModelDescriptor.CkModelId, importOperationResult);
            if (importOperationResult.HasErrors || importOperationResult.HasFatalErrors)
            {
                throw TenantException.ErrorDuringSystemModelLoad(importOperationResult);
            }
        }
        else
        {
            _logger.LogDebug(
                "No IStreamDataCkModelDescriptor registered; skipping StreamData CK model import for tenant '{TenantId}'",
                TenantId);
        }
    }

    /// <inheritdoc />
    public async Task DisableStreamDataAsync()
    {
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

        _streamDataRepository = factory.Create(TenantId);
        _streamDataRepositoryResolved = true;
        return _streamDataRepository;
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
        var tenantDatabaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, repositoryDataSource);

        // Capture schema versions BEFORE importing (for migration detection)
        var previousSchemaVersions = await GetSchemaVersionsDirectAsync(tenantDatabaseSourceIdentifier);

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
        var tenantDatabaseSourceIdentifier = new TenantDatabaseSourceIdentifier(null, repositoryDataSource);
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
            new TenantDatabaseSourceIdentifier(null, repositoryDataSource));
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
                ckEnumUpdates, new TenantDatabaseSourceIdentifier(null, repositoryDataSource), cancellationToken);
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
            _bulkRtMutation);
        return tenantRepository;
    }

    private ITenantRepository GetTenantRepositoryAsAdmin(string tenantId, string databaseName)
    {
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(databaseName, tenantId);

        var tenantRepository = new TenantRepository(tenantId, _metricsContext, _cacheService, _modelLoaderService,
            repositoryDataSource,
            _bulkRtMutation);
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

    private async Task<RtTenant?> GetRtTenantAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.NormalizeString());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, queryOptions);
        return resultSet.Items.FirstOrDefault();
    }

    private async Task<RtTenant?> GetRtSystemTenantAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.NormalizeString());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, queryOptions);
        return resultSet.Items.FirstOrDefault();
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
