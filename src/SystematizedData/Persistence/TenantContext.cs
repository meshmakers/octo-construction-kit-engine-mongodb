using CkModel.CkRuleEngine;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Microsoft.Extensions.Options;
using Persistence.Contracts;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class TenantContext : ITenantContextInternal
{
    public string TenantId { get; }

    protected readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly string _databaseName;
    protected readonly IRepositoryClient _systemRepositoryClient;
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    protected readonly ICkSystemModelService _ckSystemModelService;
    protected readonly ICkCacheService _cacheService;

    protected TenantContext(IOptions<OctoSystemConfiguration> systemConfiguration,
        string tenantId,
        string databaseName,
        ICkSystemModelService ckSystemModelService,
        ICkCacheService cacheService)
    {
        TenantId = tenantId;
        _systemConfiguration = systemConfiguration;
        _databaseName = databaseName;
        _ckSystemModelService = ckSystemModelService;
        _cacheService = cacheService;

        var systemConnectionOptions = new MongoConnectionOptions
        {
            MongoDbHost = _systemConfiguration.Value.DatabaseHost,
            MongoDbUsername = _systemConfiguration.Value.AdminUser,
            MongoDbPassword = _systemConfiguration.Value.AdminUserPassword,
            AuthenticationSource = _systemConfiguration.Value.AuthenticationDatabaseName,
            UseTls = _systemConfiguration.Value.UseTls,
            AllowInsecureTls = _systemConfiguration.Value.AllowInsecureTls
        };

        _systemRepositoryClient = new MongoRepositoryClient(systemConnectionOptions);
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
            await _cacheService.DistributeTenantModificationPreEventAsync(tenantId);

            // Create database
            await CreateTenantInternalAsync(databaseName);

            // Restore the tenant system model on the newly created repository
            var ckModelRepository = CreateTenantCkModelRepository(normalizedDatabaseName);
            await _ckSystemModelService.ImportAsync(systemSession, ckModelRepository);

            // Add the new tenant as child tenant of the current one
            var rtSystemTenant = new RtSystemTenant
            {
                TenantId = normalizedTenantId,
                DatabaseName = normalizedDatabaseName
            };

            var tenantRepository = await CreateTenantRepositoryAsync();
            await tenantRepository.InsertOneRtEntityAsync(systemSession, rtSystemTenant);

            // Distribute updates (post) to inform other services.
            await _cacheService.DistributeTenantModificationPostEventAsync(tenantId);
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
        await repository.CreateCollectionIfNotExistsAsync<CkEntity>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkEntityAssociation>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkEntityInheritance>(false);
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

        var octoTenant = new RtSystemTenant
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
        await tenantRepository.DeleteOneRtEntityAsync<RtSystemTenant>(systemSession,
            new List<FieldFilter> { new(nameof(RtSystemTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });
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

        await _cacheService.DistributeTenantModificationPreEventAsync(tenantId);
        await DropChildTenantAsync(systemSession, tenantId);
        await CreateChildTenantAsync(systemSession, octoTenant.DatabaseName, tenantId);
        await _cacheService.DistributeTenantModificationPostEventAsync(tenantId);
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

        await _cacheService.DistributeTenantModificationPreEventAsync(tenantId);
        await _systemRepositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);

        await tenantRepository.DeleteOneRtEntityAsync<RtSystemTenant>(systemSession,
            new List<FieldFilter> { new(nameof(RtSystemTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey()) });
        await _cacheService.DistributeTenantModificationPostEventAsync(tenantId);
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

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtSystemTenant>(systemSession, new DataQueryOperation(), skip, take);
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
        var context = new TenantContext(_systemConfiguration, tenantId, tenant.DatabaseName, _ckSystemModelService, _cacheService);

        await systemSession.CommitTransactionAsync();
        return context;
    }

    public ITenantCkModelRepository CreateTenantCkModelRepository()
    {
        return CreateTenantCkModelRepository(_databaseName);
    }

    public async Task<ITenantRepository> CreateOrGetTenantRepositoryAsync()
    {
        var result = await CreateOrGetTenantRepositoryInternal();
        return result;
    }

    public async Task<ITenantRepositoryInternal> CreateOrGetTenantRepositoryInternalAsync()
    {
        var result = await CreateOrGetTenantRepositoryInternal();

        return result;
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
        dataQueryOperation.AppendFieldFilter(nameof(RtSystemConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtSystemConfiguration>(systemSession, dataQueryOperation);
        var configuration = resultSet.Items.FirstOrDefault();
        if (configuration == null)
        {
            configuration = new RtSystemConfiguration { RtWellKnownName = key, ConfigurationValue = value.Serialize() };
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

    private ITenantCkModelRepository CreateTenantCkModelRepository(string databaseName)
    {
        var databaseContext = CreateDatabaseContext(databaseName);
        var tenantCkModelRepository = new TenantCkModelRepository(databaseContext);
        return tenantCkModelRepository;
    }

    private async Task<ITenantRepositoryInternal> CreateTenantRepositoryAsync()
    {
        return await CreateChildTenantRepositoryAsync(TenantId, _databaseName);
    }

    private async Task<ITenantRepositoryInternal> CreateChildTenantRepositoryAsync(string tenantId, string databaseName)
    {
        var databaseContext = CreateDatabaseContext(databaseName);
        var tenantCkModelRepository = new TenantCkModelRepository(databaseContext);
        var ckCache = await _cacheService.GetOrCreateCkCacheAsync(tenantId, tenantCkModelRepository);
        return new TenantRepository(ckCache, databaseContext);
    }


    private IDatabaseContext CreateDatabaseContext(string databaseName)
    {
        return new DatabaseContext(_systemRepositoryClient, databaseName);
    }

    private async Task<ITenantRepositoryInternal> CreateOrGetTenantRepositoryInternal()
    {
        try
        {
            await _semaphoreSlim.WaitAsync();

            var databaseContext = CreateDatabaseContext(_databaseName);
            var tenantCkModelRepository = CreateTenantCkModelRepository();
            var ckCache = await _cacheService.GetOrCreateCkCacheAsync(TenantId, tenantCkModelRepository);
            return new TenantRepository(ckCache, databaseContext);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    private async Task<RtSystemTenant?> GetRtTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        var tenantRepository = await CreateTenantRepositoryAsync();

        var dataQueryOperation = new DataQueryOperation();
        dataQueryOperation.AppendFieldFilter(nameof(RtSystemTenant.TenantId), FieldFilterOperator.Equals, tenantId.MakeKey());

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtSystemTenant>(systemSession, dataQueryOperation);
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
        dataQueryOperation.AppendFieldFilter(nameof(RtSystemConfiguration.RtWellKnownName), FieldFilterOperator.Equals, key);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtSystemConfiguration>(systemSession, dataQueryOperation);
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