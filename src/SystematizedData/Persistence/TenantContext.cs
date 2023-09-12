using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Persistence.Contracts;
using Persistence.InternalContracts;
using Persistence.SystemCkModel.ConstructionKit.Generated.System.v1;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class TenantContext : ITenantContextInternal
{
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);

    public string TenantId { get; }

    private readonly ILoggerFactory _loggerFactory;
    protected readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly string _databaseName;
    protected readonly IRepositoryClient _systemRepositoryClient;
    protected readonly ICkModelRepositoryService _ckModelRepositoryService;
    protected readonly ICkCacheService _cacheService;
    private readonly IModelLoaderService _modelLoaderService;
    protected readonly ISystemMessageService _systemMessageService;

    protected TenantContext(ILoggerFactory loggerFactory, IOptions<OctoSystemConfiguration> systemConfiguration,
        string tenantId,
        string databaseName,
        ISystemMessageService systemMessageService,
        ICkModelRepositoryService ckModelRepositoryService,
        ICkCacheService cacheService, IModelLoaderService modelLoaderService)
    {
        TenantId = tenantId;
        _loggerFactory = loggerFactory;
        _systemConfiguration = systemConfiguration;
        _databaseName = databaseName;
        _systemMessageService = systemMessageService;
        _ckModelRepositoryService = ckModelRepositoryService;
        _cacheService = cacheService;
        _modelLoaderService = modelLoaderService;

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

    public async Task<IOctoSession> StartSystemSessionAsync()
    {
        var systemSession = await _systemRepositoryClient.StartSessionAsync();
        return systemSession;
    }

    #endregion Transaction handling

    #region Tenant management

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateChildTenantAsync(IOctoSession systemSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var normalizedDatabaseName = databaseName.ToLower();
        var normalizedTenantId = tenantId.MakeKey();
        // if (await IsChildTenantExistingAsync(systemSession, normalizedTenantId))
        // {
        //      throw new TenantException($"Tenant '{normalizedTenantId}' already exists.");
        // }

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
            var rtSystemTenant = new RtTenant
            {
                TenantId = normalizedTenantId,
                DatabaseName = normalizedDatabaseName
            };

            var tenantRepository = await CreateTenantRepositoryAsync();
            await tenantRepository.InsertOneRtEntityAsync(systemSession, rtSystemTenant);

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
            throw new DatabaseException($"Database '{normalizedDatabaseName}' already exists.");
        }

        await _systemRepositoryClient.CreateRepositoryAsync(normalizedDatabaseName);
        await _systemRepositoryClient.CreateUser(_systemConfiguration.Value.AuthenticationDatabaseName,
            normalizedDatabaseName, string.Format(_systemConfiguration.Value.DatabaseUser, normalizedDatabaseName),
            _systemConfiguration.Value.DatabaseUserPassword);

        var repository = _systemRepositoryClient.GetRepository(normalizedDatabaseName);
        await repository.CreateCollectionIfNotExistsAsync<DatabaseEntities.CkModel>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkAssociationRole>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkAttribute>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkType>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkTypeAssociation>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkTypeInheritance>(false);
        await repository.CreateCollectionIfNotExistsAsync<RtAssociation>(true);
    }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task AttachChildTenantAsync(IOctoSession systemSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        if (await IsChildTenantExistingAsync(systemSession, tenantId))
        {
            throw new TenantException($"Tenant '{tenantId}' already exists.");
        }

        if (!await IsDatabaseAlreadyExistingAsync(databaseName))
        {
            throw new DatabaseException($"Database '{databaseName}' does not exist.");
        }

        var octoTenant = new RtTenant
        {
            TenantId = tenantId,
            DatabaseName = databaseName
        };

        var tenantRepository = await CreateTenantRepositoryAsync();
        await tenantRepository.InsertOneRtEntityAsync(systemSession, octoTenant);
    }

    public async Task DetachChildTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exists.");
        }

        var tenantRepository = await CreateTenantRepositoryAsync();
        await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(systemSession,
            new List<FieldFilter> { new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearChildTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await _systemMessageService.DistributeTenantModificationPreEventAsync(tenantId);
        await DropChildTenantAsync(systemSession, tenantId);
        await CreateChildTenantAsync(systemSession, octoTenant.DatabaseName, tenantId);
        await _systemMessageService.DistributeTenantModificationPostEventAsync(tenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task DropChildTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenantRepository = await CreateTenantRepositoryAsync();

        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await _systemMessageService.DistributeTenantModificationPreEventAsync(tenantId);
        await _systemRepositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);

        await tenantRepository.DeleteOneRtEntityAsync<RtTenant>(systemSession,
            new List<FieldFilter> { new(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });
        await _systemMessageService.DistributeTenantModificationPostEventAsync(tenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsChildTenantExistingAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetRtTenantAsync(systemSession, tenantId);
        return octoTenant != null;
    }

    public async Task<PagedResult<OctoTenant>> GetChildTenantsAsync(IOctoSession systemSession, int? skip = null,
        int? take = null)
    {
        var tenantRepository = await CreateTenantRepositoryAsync();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(systemSession, new DataQueryOperation(), skip, take);
        return new PagedResult<OctoTenant>(result.Items.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            skip, take, result.TotalCount);
    }

    public async Task<OctoTenant> GetChildTenantAsync(IOctoSession systemSession, string tenantId)
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

    public async Task<ITenantContext> CreateChildTenantContextAsync(string tenantId)
    {
        return await CreateChildTenantContextInternalAsync(tenantId);
    }

    public async Task<ITenantContextInternal> CreateChildTenantContextInternalAsync(string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        using var systemSession = await StartSystemSessionAsync();
        systemSession.StartTransaction();

        var tenant = await GetChildTenantAsync(systemSession, tenantId);
        var context = new TenantContext(_loggerFactory, _systemConfiguration, tenantId, tenant.DatabaseName, _systemMessageService,
            _ckModelRepositoryService, _cacheService, _modelLoaderService);

        await systemSession.CommitTransactionAsync();
        return context;
    }

    public ITenantRepository CreateOrGetTenantRepository()
    {
        var result = CreateOrGetTenantRepositoryInternal();
        return result;
    }

    public ITenantRepositoryInternal CreateOrGetTenantRepositoryInternal()
    {
        try
        {
            _semaphoreSlim.Wait();

            var databaseContext = CreateDatabaseContext(_databaseName);
            return new TenantRepository(TenantId, _cacheService, databaseContext);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    #endregion Access management

    #region Configuration

    public async Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoSession systemSession, string key,
        TValueType? defaultValue) where
        TValueType
        : class
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        var o = await GetConfigAsync(systemSession, key, defaultValue);

        return o;
    }

    public async Task<string?> GetConfigurationAsync(IOctoSession systemSession, string key, string? defaultValue = null)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        return await GetConfigAsync(systemSession, key, defaultValue);
    }


    public async Task SetConfigurationAsync<TValueType>(IOctoSession systemSession, string key, TValueType value)
        where TValueType : struct

    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigurationAsync(systemSession, key, (object)value);
    }

    public async Task SetConfigurationAsync(IOctoSession systemSession, string key, string value)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigurationAsync(systemSession, key, (object)value);
    }

    public async Task SetConfigurationAsync(IOctoSession systemSession, string key, object value)
    {
        var tenantRepository = await CreateTenantRepositoryAsync();

        var dataQueryOperation = new DataQueryOperation();
        dataQueryOperation.AppendFieldFilter(nameof(RtConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

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

    #region Private methods

    private async Task<ITenantRepositoryInternal> CreateTenantRepositoryAsync()
    {
        return await CreateChildTenantRepositoryAsync(TenantId, _databaseName);
    }

    private async Task<ITenantRepositoryInternal> CreateChildTenantRepositoryAsync(string tenantId, string databaseName)
    {
        var databaseContext = CreateDatabaseContext(databaseName);
        using var session = await databaseContext.StartSessionAsync();
        await _modelLoaderService.LoadAsync(tenantId, session, databaseContext);
        
        return new TenantRepository(tenantId, _cacheService, databaseContext);
    }


    protected IDatabaseContext CreateDatabaseContext(string databaseName)
    {
        return new DatabaseContext(_systemRepositoryClient, databaseName);
    }

    private async Task<RtTenant?> GetRtTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        var tenantRepository = await CreateTenantRepositoryAsync();

        var dataQueryOperation = new DataQueryOperation();
        dataQueryOperation.AppendFieldFilter(nameof(RtTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(systemSession, dataQueryOperation);
        return resultSet.Items.FirstOrDefault();
    }

    protected async Task<bool> IsDatabaseAlreadyExistingAsync(string databaseName)
    {
        return await _systemRepositoryClient.IsRepositoryExistingAsync(databaseName);
    }

    private async Task<TType?> GetConfigAsync<TType>(IOctoSession systemSession, string key, TType? defaultValue)
    {
        var tenantRepository = await CreateTenantRepositoryAsync();

        var dataQueryOperation = new DataQueryOperation();
        dataQueryOperation.AppendFieldFilter(nameof(RtConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

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