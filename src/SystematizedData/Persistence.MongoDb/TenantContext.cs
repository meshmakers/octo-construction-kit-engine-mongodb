using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Persistence.Contracts;
using Persistence.InternalContracts;
using Persistence.SystemCkModel.ConstructionKit.Generated.System.v1;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class TenantContext : ITenantContext
{
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);

    public string TenantId { get; }

    private readonly ILoggerFactory _loggerFactory;
    protected readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly string _databaseName;
    protected readonly IRepositoryClient _systemRepositoryClient;
    protected readonly ICkModelRepositoryService _ckModelRepositoryService;
    private readonly ICkCacheService _cacheService;
    private readonly IModelLoaderService _modelLoaderService;
    private readonly IBulkRtMutation _bulkRtMutation;
    protected readonly ISystemMessageService _systemMessageService;

    protected TenantContext(ILoggerFactory loggerFactory, IOptions<OctoSystemConfiguration> systemConfiguration,
        string tenantId,
        string databaseName,
        ISystemMessageService systemMessageService,
        ICkModelRepositoryService ckModelRepositoryService,
        ICkCacheService cacheService, IModelLoaderService modelLoaderService, IBulkRtMutation bulkRtMutation)
    {
        TenantId = tenantId;
        _loggerFactory = loggerFactory;
        _systemConfiguration = systemConfiguration;
        _databaseName = databaseName;
        _systemMessageService = systemMessageService;
        _ckModelRepositoryService = ckModelRepositoryService;
        _cacheService = cacheService;
        _modelLoaderService = modelLoaderService;
        _bulkRtMutation = bulkRtMutation;

        var systemConnectionOptions = new MongoConnectionOptions
        {
            MongoDbHost = _systemConfiguration.Value.DatabaseHost,
            MongoDbUsername = _systemConfiguration.Value.AdminUser,
            MongoDbPassword = _systemConfiguration.Value.AdminUserPassword,
            AuthenticationSource = _systemConfiguration.Value.AuthenticationDatabaseName,
            UseTls = _systemConfiguration.Value.UseTls,
            AllowInsecureTls = _systemConfiguration.Value.AllowInsecureTls
        };

        _systemRepositoryClient = new MongoRepositoryClient(loggerFactory.CreateLogger<MongoRepositoryClient>(), systemConnectionOptions);
    }

    #region Transaction handling

    public async Task<IOctoSystemSession> GetSystemSessionAsync()
    {
        var systemSession = await _systemRepositoryClient.GetSessionAsync();
        return systemSession;
    }

    #endregion Transaction handling

    #region Tenant management

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateChildTenantAsync(IOctoSystemSession systemSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var normalizedDatabaseName = databaseName.ToLower();
        var normalizedTenantId = tenantId.MakeKey();
        if (await IsTenantExistingAsync(systemSession, normalizedTenantId))
        {
            throw TenantException.TenantDoesAlreadyExist(tenantId);
        }

        try
        {
            // Distribute updates (post) to inform other services.
            await _systemMessageService.DistributeTenantModificationPreEventAsync(tenantId);

            // Create database
            await CreateTenantInternalAsync(databaseName);

            // Restore the tenant system model on the newly created repository
            var databaseContext = CreateDatabaseContext(normalizedDatabaseName);
            OperationResult operationResult = new();
            var ckCompiledModelRoot = await _ckModelRepositoryService.LookupCkModelAsync(SystemCkIds.ModelId, operationResult);
            if (ckCompiledModelRoot == null)
            {
                throw TenantException.SystemModelNotFound();
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw TenantException.ErrorDuringSystemModelLoad(operationResult);
            }

            await _ckModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, true,
                new TenantDatabaseSourceIdentifier(databaseContext, systemSession));

            // Add the new tenant as child tenant of the current one
            if (TenantId != _systemConfiguration.Value.SystemTenantId.MakeKey())
            {
                var rtTenant = new RtTenant
                {
                    TenantId = normalizedTenantId,
                    DatabaseName = normalizedDatabaseName
                };

                var tenantRepository = await GetTenantRepositoryAsync();
                await tenantRepository.InsertOneRtEntityAsync(systemSession, rtTenant);
            }

            // Add the new tenant in system tenant to be found in future operations
            var rtSystemTenant = new RtTenant
            {
                TenantId = normalizedTenantId,
                ParentTenantId = TenantId,
                DatabaseName = normalizedDatabaseName
            };
            var systemTenantRepository = await GetSystemTenantRepositoryAsync();
            await systemTenantRepository.InsertOneRtEntityAsync(systemSession, rtSystemTenant);

            // Distribute updates (post) to inform other services.
            await _systemMessageService.DistributeTenantModificationPostEventAsync(tenantId);
        }
        catch (Exception)
        {
            await _systemRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            throw;
        }
    }


    protected async Task CreateTenantInternalAsync(string databaseName)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);

        var normalizedDatabaseName = databaseName.ToLower();

        if (await IsDatabaseAlreadyExistingAsync(normalizedDatabaseName))
        {
            throw TenantException.TenantDatabaseDoesAlreadyExist(normalizedDatabaseName);
        }

        await _systemRepositoryClient.CreateRepositoryAsync(normalizedDatabaseName);
        await _systemRepositoryClient.CreateUser(_systemConfiguration.Value.AuthenticationDatabaseName,
            normalizedDatabaseName, string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName),
            _systemConfiguration.Value.DatabaseUserPassword);

        var repository = _systemRepositoryClient.GetRepository(normalizedDatabaseName);

        await repository.CreateCollectionIfNotExistsAsync<RtAssociation>(true);
    }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task AttachChildTenantAsync(IOctoSystemSession systemSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var normalizedDatabaseName = databaseName.ToLower();
        var normalizedTenantId = tenantId.MakeKey();

        if (await IsTenantExistingAsync(systemSession, tenantId))
        {
            throw TenantException.TenantDoesAlreadyExist(tenantId);
        }

        if (!await IsDatabaseAlreadyExistingAsync(databaseName))
        {
            throw TenantException.TenantDatabaseDoesNotExist(databaseName);
        }

        // Add the new tenant as child tenant of the current one
        if (TenantId != _systemConfiguration.Value.SystemTenantId.MakeKey())
        {
            var octoTenant = new RtTenant
            {
                TenantId = tenantId,
                DatabaseName = databaseName
            };

            var tenantRepository = await GetTenantRepositoryAsync();
            await tenantRepository.InsertOneRtEntityAsync(systemSession, octoTenant);
        }

        // Add the new tenant in system tenant to be found in future operations
        var rtSystemTenant = new RtTenant
        {
            TenantId = normalizedTenantId,
            ParentTenantId = TenantId,
            DatabaseName = normalizedDatabaseName
        };
        var systemTenantRepository = await GetSystemTenantRepositoryAsync();
        await systemTenantRepository.InsertOneRtEntityAsync(systemSession, rtSystemTenant);
    }

    public async Task DetachChildTenantAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exists.");
        }

        var tenantRepository = await GetTenantRepositoryAsync();
        await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(systemSession,
            new List<FieldFilter> { new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearChildTenantAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }

        await _systemMessageService.DistributeTenantModificationPreEventAsync(tenantId);
        await DropChildTenantAsync(systemSession, tenantId);
        await CreateChildTenantAsync(systemSession, octoTenant.DatabaseName, tenantId);
        await _systemMessageService.DistributeTenantModificationPostEventAsync(tenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task DropChildTenantAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenantRepository = await GetTenantRepositoryAsync();

        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await _systemMessageService.DistributeTenantModificationPreEventAsync(tenantId);
        await _systemRepositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);

        // Deletes the tenant entry from the current tenant
        await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(systemSession,
            new List<FieldFilter> { new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });

        // If the current tenant is not the system tenant, we need to delete the tenant entry in system tenant too.
        // Add the new tenant as child tenant of the current one
        if (TenantId != _systemConfiguration.Value.SystemTenantId.MakeKey())
        {
            var systemTenantRepository = await GetSystemTenantRepositoryAsync();
            await systemTenantRepository.DeleteOneRtEntityAsync<RtTenant>(systemSession,
                new List<FieldFilter> { new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });
        }

        await _systemMessageService.DistributeTenantModificationPostEventAsync(tenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsChildTenantExistingAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        return octoTenant != null;
    }

    public async Task<bool> IsTenantExistingAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtSystemTenantAsync(systemSession, tenantId);
        return octoTenant != null;
    }

    public async Task<PagedResult<OctoTenant>> GetChildTenantsAsync(IOctoSystemSession systemSession, int? skip = null,
        int? take = null)
    {
        var tenantRepository = await GetTenantRepositoryAsync();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(systemSession, new DataQueryOperation(), skip, take);
        return new PagedResult<OctoTenant>(result.Items.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            skip, take, result.TotalCount);
    }

    public async Task<OctoTenant> GetChildTenantAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var normalizedTenantId = tenantId.MakeKey();

        var rtSystemTenant = await GetRtTenantAsync(systemSession, normalizedTenantId);
        if (rtSystemTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' not found.");
        }

        return new OctoTenant(rtSystemTenant.TenantId, rtSystemTenant.DatabaseName);
    }

    #endregion Tenant management

    #region Access management

    public async Task<ITenantContext> GetChildTenantContextAsync(string tenantId)
    {
        using var systemSession = await GetSystemSessionAsync();
        systemSession.StartTransaction();
        
        var context = await GetChildTenantContextAsync(systemSession, tenantId);
        
        await systemSession.CommitTransactionAsync();

        return context;
    }

    public async Task<ITenantContext> GetChildTenantContextAsync(IOctoSystemSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenant = await GetChildTenantAsync(systemSession, tenantId);
        var context = new TenantContext(_loggerFactory, _systemConfiguration, tenantId, tenant.DatabaseName, _systemMessageService,
            _ckModelRepositoryService, _cacheService, _modelLoaderService, _bulkRtMutation);

        return context;
    }

    public async Task<ITenantRepository> GetSystemTenantRepositoryAsync()
    {
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.MakeKey();
        
        var result = await GetTenantRepositoryAsync(normalizedTenantId, normalizedDatabaseName);
        return result;
    }

    public async Task<ITenantRepository> GetTenantRepositoryAsync()
    {
        var result = await GetTenantRepositoryAsync(TenantId, _databaseName);
        return result;
    }

    #endregion Access management

    #region Configuration

    public async Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoSystemSession systemSession, string key,
        TValueType? defaultValue) where
        TValueType
        : class
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        var o = await GetConfigAsync(systemSession, key, defaultValue);

        return o;
    }

    public async Task<string?> GetConfigurationAsync(IOctoSystemSession systemSession, string key, string? defaultValue = null)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        return await GetConfigAsync(systemSession, key, defaultValue);
    }


    public async Task SetConfigurationAsync<TValueType>(IOctoSystemSession systemSession, string key, TValueType value)
        where TValueType : struct

    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigurationAsync(systemSession, key, (object)value);
    }

    public async Task SetConfigurationAsync(IOctoSystemSession systemSession, string key, string value)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigurationAsync(systemSession, key, (object)value);
    }

    public async Task SetConfigurationAsync(IOctoSystemSession systemSession, string key, object value)
    {
        var tenantRepository = await GetTenantRepositoryAsync();

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtConfiguration>(systemSession, dataQueryOperation);
        var configuration = resultSet.Items.FirstOrDefault();
        if (configuration == null)
        {
            configuration = new RtConfiguration { RtWellKnownName = key, ConfigurationValue = value.Serialize() };
            await tenantRepository.InsertOneRtEntityAsync(systemSession, configuration);
        }

        else
        {
            configuration.ConfigurationValue = value.Serialize();
            await tenantRepository.ReplaceOneRtEntityByIdAsync(systemSession, configuration.RtId, configuration);
        }
    }

    #endregion Configuration

    #region Construction Kits

    public async Task ImportCkModelAsync(IOctoSystemSession systemSession, CkCompiledModelRoot ckCompiledModelRoot)
    {
        try
        {
            await _systemMessageService.DistributeTenantModificationPreEventAsync(TenantId);
            var databaseContext = CreateDatabaseContext(_databaseName);
            await _ckModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, false,
                new TenantDatabaseSourceIdentifier(databaseContext, systemSession));
        }
        finally
        {
            await _systemMessageService.DistributeTenantModificationPostEventAsync(TenantId);
        }
    }

    public async Task ImportCkModelAsync(IOctoSystemSession systemSession, CkModelId ckModelId, OperationResult operationResult)
    {
        var ckCompiledModelRoot = await _ckModelRepositoryService.LookupCkModelAsync(ckModelId, operationResult);
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
            await _systemMessageService.DistributeTenantModificationPreEventAsync(TenantId);
            var databaseContext = CreateDatabaseContext(_databaseName);
            await _ckModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, false,
                new TenantDatabaseSourceIdentifier(databaseContext, systemSession));
        }
        finally
        {
            await _systemMessageService.DistributeTenantModificationPostEventAsync(TenantId);
        }
    }

    #endregion

    #region Private methods

    private async Task<ITenantRepository> GetTenantRepositoryAsync(string tenantId, string databaseName)
    {
        try
        {
            await _semaphoreSlim.WaitAsync();

            var databaseContext = CreateDatabaseContext(databaseName);
            var session = await databaseContext.GetSessionAsync();
            session.StartTransaction();

            await _modelLoaderService.LoadAsync(tenantId, session, databaseContext);
            var tenantRepository = new TenantRepository(tenantId, _cacheService, databaseContext, _bulkRtMutation);

            await session.CommitTransactionAsync();

            return tenantRepository;
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }


    protected IDatabaseContext CreateDatabaseContext(string databaseName)
    {
        return new DatabaseContext(_systemRepositoryClient, databaseName, TenantId);
    }

    private async Task<RtTenant?> GetRtTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        var tenantRepository = await GetTenantRepositoryAsync();

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(systemSession, dataQueryOperation);
        return resultSet.Items.FirstOrDefault();
    }

    private async Task<RtTenant?> GetRtSystemTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        var tenantRepository = await GetSystemTenantRepositoryAsync();

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(systemSession, dataQueryOperation);
        return resultSet.Items.FirstOrDefault();
    }

    protected async Task<bool> IsDatabaseAlreadyExistingAsync(string databaseName)
    {
        return await _systemRepositoryClient.IsRepositoryExistingAsync(databaseName);
    }

    private async Task<TType?> GetConfigAsync<TType>(IOctoSession systemSession, string key, TType? defaultValue)
    {
        var tenantRepository = await GetTenantRepositoryAsync();

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