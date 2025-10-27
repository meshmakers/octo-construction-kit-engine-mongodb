using System.Diagnostics;

using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
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
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;

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
        _adminRepositoryAccess = serviceProvider.GetRequiredService<IAdminRepositoryAccess>();
        _adminRepositoryClient = _adminRepositoryAccess.GetRepositoryClient(databaseName);
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
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName);
        await repositoryDataSource.CreateRtAssociationIndexesAsync();
    }

    public async Task UpdateIndexesAsync(IOctoAdminSession adminSession)
    {
        _logger.LogInformation("Updating indexes for tenant {TenantId} in database {DatabaseName}", TenantId,
            DatabaseName);

        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(DatabaseName);
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
            await UpdateSystemCkModelAsync(normalizedDatabaseName, true);

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

    protected async Task UpdateSystemCkModelAsync(string normalizedDatabaseName, bool isRepositoryInCreation = false)
    {
        var databaseContext = CreateRepositoryDataSourceAsAdmin(normalizedDatabaseName);
        var databaseSourceIdentifier = new TenantDatabaseSourceIdentifier(databaseContext);
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

            await _ckModelRepositoryService.UpdateModelAsync(
                ckCompiledModelRoot, databaseSourceIdentifier);
        }
        finally
        {
            if (!isRepositoryInCreation)
            {
                await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
            }

            _logger.LogInformation("System CK Model restored into tenant '{TenantId}'", TenantId);
        }
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

        await UpdateSystemCkModelAsync(tenant.DatabaseName);

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

        try
        {
            _logger.LogInformation("Importing CK Model '{CkModelId}' into tenant '{TenantId}'",
                ckCompiledModelRoot.ModelId, TenantId);

            await _tenantNotifications.NotifyPreTenantUpdateAsync(TenantId, correlationId);
            var repositoryDataSource = CreateRepositoryDataSource(DatabaseName);
            await _ckModelRepositoryService.UpdateModelAsync(ckCompiledModelRoot,
                new TenantDatabaseSourceIdentifier(repositoryDataSource));

            _logger.LogInformation("CK Model '{CkModelId}' imported into tenant '{TenantId}'",
                ckCompiledModelRoot.ModelId, TenantId);
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
        }
    }

    public async Task ImportCkModelAsync(CkModelId ckModelId, OperationResult operationResult)
    {
        Guid correlationId = Guid.NewGuid();

        var repositoryDataSource = CreateRepositoryDataSource(DatabaseName);
        var tenantDatabaseSourceIdentifier = new TenantDatabaseSourceIdentifier(repositoryDataSource);
        if (await _ckModelRepositoryService.IsExistingAsync(ckModelId, tenantDatabaseSourceIdentifier))
        {
            _logger.LogDebug("CK Model '{CkModelId}' already exists in tenant '{TenantId}', skipping import",
                ckModelId, TenantId);
            return;
        }

        try
        {
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

            await _ckModelRepositoryService.UpdateModelAsync(ckCompiledModelRoot, tenantDatabaseSourceIdentifier);

            _logger.LogInformation("CK Model '{CkModelId}' imported into tenant '{TenantId}'", ckModelId, TenantId);
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantUpdateAsync(TenantId, correlationId);
        }
    }

    public async Task<bool> IsCkModelExistingAsync(CkModelId ckModelId)
    {
        var repositoryDataSource = CreateRepositoryDataSource(DatabaseName);

        var r = await _ckModelRepositoryService.IsExistingAsync(
            ckModelId.ToVersionRange(),
            new TenantDatabaseSourceIdentifier(repositoryDataSource));
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
                ckEnumUpdates, new TenantDatabaseSourceIdentifier(repositoryDataSource), cancellationToken);
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
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(databaseName);

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

    protected IMongoDbRepositoryDataSource CreateRepositoryDataSourceAsAdmin(string databaseName)
    {
        return new MongoDbRepositoryDataSource(_loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _adminRepositoryClient, databaseName, TenantId);
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
