using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
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

public class TenantContext : ITenantContext
{
    private readonly IBulkRtMutation _bulkRtMutation;
    private readonly ICkCacheService _cacheService;

    private readonly string _databaseName;
    private readonly ILoggerFactory _loggerFactory;

    private readonly IMetricsContext _metricsContext;
    private readonly IModelLoaderService _modelLoaderService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;

    protected readonly ICkModelRepositoryService CkModelRepositoryService;
    protected readonly IAdminRepositoryClient AdminRepositoryClient;
    protected readonly IOptions<OctoSystemConfiguration> SystemConfiguration;
    protected readonly ITenantNotifications TenantNotifications;

    protected TenantContext(ILoggerFactory loggerFactory, IOptions<OctoSystemConfiguration> systemConfiguration,
        IServiceProvider serviceProvider, string tenantId, string databaseName)
    {
        TenantId = tenantId;
        _metricsContext = serviceProvider.GetRequiredService<IMetricsContext>();
        _loggerFactory = loggerFactory;
        SystemConfiguration = systemConfiguration;
        _serviceProvider = serviceProvider;
        _databaseName = databaseName;
        TenantNotifications = serviceProvider.GetRequiredService<ITenantNotifications>();
        CkModelRepositoryService = serviceProvider.GetRequiredService<ICkModelRepositoryService>();
        _cacheService = serviceProvider.GetRequiredService<ICkCacheService>();
        _modelLoaderService = serviceProvider.GetRequiredService<IModelLoaderService>();
        _bulkRtMutation = serviceProvider.GetRequiredService<IBulkRtMutation>();
        _adminRepositoryAccess = serviceProvider.GetRequiredService<IAdminRepositoryAccess>();
        AdminRepositoryClient = _adminRepositoryAccess.GetRepositoryClient(databaseName);
    }

    public string TenantId { get; }

    #region Transaction handling

    public async Task<IOctoAdminSession> GetAdminSessionAsync()
    {
        var adminSession = await AdminRepositoryClient.GetAdminSessionAsync();
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

        try
        {
            // Distribute updates (pre) to inform other services.
            await TenantNotifications.NotifyPreTenantCreateAsync(tenantId);

            // Create database
            await CreateTenantInternalAsync(databaseName);

            // Restore the tenant system model on the newly created repository
            var databaseContext = CreateRepositoryDataSourceAsAdmin(normalizedDatabaseName);
            OperationResult operationResult = new();
            var ckCompiledModelRoot = await CkModelRepositoryService.LookupCkModelAsync(SystemCkIds.ModelId, operationResult);
            if (ckCompiledModelRoot == null)
            {
                throw TenantException.SystemModelNotFound();
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw TenantException.ErrorDuringSystemModelLoad(operationResult);
            }

            await CkModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, true,
                new TenantDatabaseSourceIdentifier(databaseContext));

            // Add the new tenant as child tenant of the current one
            if (TenantId != SystemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var rtTenant = new RtTenant
                {
                    TenantId = normalizedTenantId,
                    DatabaseName = normalizedDatabaseName
                };

                var tenantRepository = GetTenantRepositoryAsAdmin();
                await tenantRepository.InsertOneRtEntityAsync(adminSession, rtTenant);
            }

            // Add the new tenant in system tenant to be found in future operations
            var rtSystemTenant = new RtTenant
            {
                TenantId = normalizedTenantId,
                ParentTenantId = TenantId,
                DatabaseName = normalizedDatabaseName
            };
            var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
            await systemTenantRepository.InsertOneRtEntityAsync(adminSession, rtSystemTenant);
        }
        catch (Exception)
        {
            await AdminRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            throw;
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await TenantNotifications.NotifyPosTenantCreateAsync(tenantId);
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

        await AdminRepositoryClient.CreateRepositoryAsync(normalizedDatabaseName);
        await AdminRepositoryClient.CreateUser(SystemConfiguration.Value.AuthenticationDatabaseName,
            normalizedDatabaseName, string.Format(SystemConfiguration.Value.DatabaseUser, normalizedDatabaseName),
            SystemConfiguration.Value.DatabaseUserPassword);
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

        try
        {
            // Distribute updates (pre) to inform other services.
            await TenantNotifications.NotifyPreTenantCreateAsync(tenantId);

            // Add the new tenant as child tenant of the current one
            if (TenantId != SystemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var octoTenant = new RtTenant
                {
                    TenantId = tenantId,
                    DatabaseName = databaseName
                };

                var tenantRepository = GetTenantRepositoryAsAdmin();
                await tenantRepository.InsertOneRtEntityAsync(adminSession, octoTenant);
            }

            // Add the new tenant in system tenant to be found in future operations
            var rtSystemTenant = new RtTenant
            {
                TenantId = normalizedTenantId,
                ParentTenantId = TenantId,
                DatabaseName = normalizedDatabaseName
            };
            var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
            await systemTenantRepository.InsertOneRtEntityAsync(adminSession, rtSystemTenant);
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await TenantNotifications.NotifyPosTenantCreateAsync(tenantId);
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

        try
        {
            // Distribute updates (pre) to inform other services.
            await TenantNotifications.NotifyPreTenantDeleteAsync(tenantId);

            var tenantRepository = GetTenantRepositoryAsAdmin();
            await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                new List<FieldFilter>
                {
                    new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals,
                        tenantId.NormalizeString())
                });
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await TenantNotifications.NotifyPosTenantDeleteAsync(tenantId);
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

        try
        {
            await TenantNotifications.NotifyPreTenantUpdateAsync(tenantId);

            await DropChildTenantAsync(adminSession, tenantId);
            await CreateChildTenantAsync(adminSession, octoTenant.DatabaseName, tenantId);
        }
        finally
        {
            await TenantNotifications.NotifyPosTenantUpdateAsync(tenantId);
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

        try
        {
            await TenantNotifications.NotifyPreTenantDeleteAsync(tenantId);

            await AdminRepositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);

            // Deletes the tenant entry from the current tenant
            await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                new List<FieldFilter>
                {
                    new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals,
                        tenantId.NormalizeString())
                });

            // If the current tenant is not the system tenant, we need to delete the tenant entry in system tenant too.
            // Add the new tenant as child tenant of the current one
            if (TenantId != SystemConfiguration.Value.SystemTenantId.NormalizeString())
            {
                var systemTenantRepository = GetSystemTenantRepositoryAsAdmin();
                await systemTenantRepository.DeleteOneRtEntityAsync<RtTenant>(adminSession,
                    new List<FieldFilter>
                    {
                        new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals,
                            tenantId.NormalizeString())
                    });
            }
        }
        finally
        {
            await TenantNotifications.NotifyPosTenantDeleteAsync(tenantId);
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

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, DataQueryOperation.Create(), skip, take);
        return new ResultSet<OctoTenant>(result.Items.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            result.TotalCount, null);
    }

    public async Task<OctoTenant> GetChildTenantAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var normalizedTenantId = tenantId.NormalizeString();

        var rtSystemTenant = await GetRtTenantAsync(adminSession, normalizedTenantId);
        if (rtSystemTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        return new OctoTenant(rtSystemTenant.TenantId, rtSystemTenant.DatabaseName);
    }

    #endregion Tenant management

    #region Access management

    public async Task<ITenantContext> GetChildTenantContextAsync(string tenantId)
    {
        using var systemSession = await GetAdminSessionAsync();
        systemSession.StartTransaction();

        var context = await GetChildTenantContextAsync(systemSession, tenantId);

        await systemSession.CommitTransactionAsync();

        return context;
    }

    public async Task<ITenantContext> GetChildTenantContextAsync(IOctoAdminSession adminSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenant = await GetChildTenantAsync(adminSession, tenantId);
        var context = new TenantContext(_loggerFactory, SystemConfiguration, _serviceProvider, tenantId, tenant.DatabaseName);

        return context;
    }

    public ITenantRepository GetSystemTenantRepository()
    {
        var normalizedDatabaseName = SystemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = SystemConfiguration.Value.SystemTenantId.NormalizeString();

        var result = GetTenantRepository(normalizedTenantId, normalizedDatabaseName);
        return result;
    }

    private ITenantRepository GetSystemTenantRepositoryAsAdmin()
    {
        var normalizedDatabaseName = SystemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = SystemConfiguration.Value.SystemTenantId.NormalizeString();

        var result = GetTenantRepositoryAsAdmin(normalizedTenantId, normalizedDatabaseName);
        return result;
    }


    public ITenantRepository GetTenantRepository()
    {
        var result = GetTenantRepository(TenantId, _databaseName);
        return result;
    }

    private ITenantRepository GetTenantRepositoryAsAdmin()
    {
        var result = GetTenantRepositoryAsAdmin(TenantId, _databaseName);
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

    public async Task<string?> GetConfigurationAsync(IOctoAdminSession adminSession, string key, string? defaultValue = null)
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

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtConfiguration>(adminSession, dataQueryOperation);
        var configuration = resultSet.Items.FirstOrDefault();
        if (configuration == null)
        {
            configuration = new RtConfiguration { RtWellKnownName = key, ConfigurationValue = value.Serialize() };
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

        var fieldFilters = new List<FieldFilter>
            { new(nameof(RtConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key) };

        await tenantRepository.DeleteOneRtEntityAsync<RtConfiguration>(adminSession, fieldFilters);
    }

    #endregion Configuration

    #region Construction Kits

    public async Task ImportCkModelAsync(CkCompiledModelRoot ckCompiledModelRoot)
    {
        try
        {
            await TenantNotifications.NotifyPreTenantUpdateAsync(TenantId);
            var repositoryDataSource = CreateRepositoryDataSource(_databaseName);
            await CkModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, false,
                new TenantDatabaseSourceIdentifier(repositoryDataSource));
        }
        finally
        {
            await TenantNotifications.NotifyPosTenantUpdateAsync(TenantId);
        }
    }

    public async Task ImportCkModelAsync(CkModelId ckModelId, OperationResult operationResult)
    {
        var ckCompiledModelRoot = await CkModelRepositoryService.LookupCkModelAsync(ckModelId, operationResult);
        if (ckCompiledModelRoot == null)
        {
            throw TenantException.ModelNotFound(ckModelId);
        }

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw TenantException.ErrorDuringModelLoad(ckModelId, operationResult);
        }

        try
        {
            await TenantNotifications.NotifyPreTenantUpdateAsync(TenantId);
            var repositoryDataSource = CreateRepositoryDataSource(_databaseName);
            await CkModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, false,
                new TenantDatabaseSourceIdentifier(repositoryDataSource));
        }
        finally
        {
            await TenantNotifications.NotifyPosTenantUpdateAsync(TenantId);
        }
    }

    public async Task<bool> IsCkModelExistingAsync(CkModelId ckModelId)
    {
        var repositoryDataSource = CreateRepositoryDataSource(_databaseName);

        return await CkModelRepositoryService.IsCkModelExistingAsync(InternalConstants.CkModelRepositoryName, ckModelId,
            new TenantDatabaseSourceIdentifier(repositoryDataSource));
    }

    #endregion

    #region Private methods

    private ITenantRepository GetTenantRepository(string tenantId, string databaseName)
    {
        var repositoryDataSource = CreateRepositoryDataSource(databaseName);

        var tenantRepository = new TenantRepository(tenantId, _metricsContext, _cacheService, _modelLoaderService, repositoryDataSource,
            _bulkRtMutation);
        return tenantRepository;
    }
    
    private ITenantRepository GetTenantRepositoryAsAdmin(string tenantId, string databaseName)
    {
        var repositoryDataSource = CreateRepositoryDataSourceAsAdmin(databaseName);

        var tenantRepository = new TenantRepository(tenantId, _metricsContext, _cacheService, _modelLoaderService, repositoryDataSource,
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
            AdminRepositoryClient, databaseName, TenantId);
    }

    private async Task<RtTenant?> GetRtTenantAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.NormalizeString());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, dataQueryOperation);
        return resultSet.Items.FirstOrDefault();
    }

    private async Task<RtTenant?> GetRtSystemTenantAsync(IOctoAdminSession adminSession,
        string tenantId)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.NormalizeString());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession, dataQueryOperation);
        return resultSet.Items.FirstOrDefault();
    }

    protected async Task<bool> IsDatabaseExistingAsync(string databaseName)
    {
        return await AdminRepositoryClient.IsRepositoryExistingAsync(databaseName);
    }

    private async Task<TType?> GetConfigAsync<TType>(IOctoSession systemSession, string key, TType? defaultValue)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtConfiguration>(systemSession, dataQueryOperation);
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