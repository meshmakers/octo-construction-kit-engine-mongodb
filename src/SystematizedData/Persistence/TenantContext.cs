using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class TenantContext : ITenantContextInternal
{
    public string TenantId { get; }

    internal protected readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly string _databaseName;
    internal protected  readonly IRepositoryClient _systemRepositoryClient;
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    internal protected readonly ICkSystemModelService _ckSystemModelService;
    internal protected readonly ICkCacheService _cacheService;

    public TenantContext(IOptions<OctoSystemConfiguration> systemConfiguration,
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
        var systemSession = await _systemRepositoryClient.GetRepository(_databaseName).StartSessionAsync();
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
        if (await IsChildTenantExistingAsync(systemSession, normalizedTenantId))
        {
            throw new TenantException($"Tenant '{normalizedTenantId}' already exists.");
        }

        try
        {
            await _cacheService.DistributeTenantModificationPreEventAsync(tenantId);

            await CreateTenantInternalAsync(databaseName);

            var rtSystemTenant = new RtSystemTenant
            {
                TenantId = normalizedTenantId,
                DatabaseName = normalizedDatabaseName
            };
            
            var tenantRepository = await CreateCurrentTenantRepositoryAsync();

            await tenantRepository.InsertOneRtEntityAsync(systemSession, rtSystemTenant);
            await RestoreTenantSystemCkModelAsync(systemSession, rtSystemTenant);
            await _cacheService.DistributeTenantModificationPostEventAsync(tenantId);
        }
        catch (Exception)
        {
            await _systemRepositoryClient.DropRepositoryAsync(_systemConfiguration.Value.SystemDatabaseName);
            throw;
        }
    }
    

    private async Task CreateTenantInternalAsync(string databaseName)
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

        var tenantRepository = await CreateCurrentTenantRepositoryAsync();
        await tenantRepository.InsertOneRtEntityAsync(systemSession, octoTenant);
    }

    public async Task DetachChildTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exists.");
        }

        var tenantRepository = await CreateCurrentTenantRepositoryAsync();
        await tenantRepository.DeleteOneRtEntityAsync<RtSystemTenant>(systemSession,
            rtSystemTenant => rtSystemTenant.TenantId == tenantId.MakeKey());
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearChildTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
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
        
        var tenantRepository = await CreateCurrentTenantRepositoryAsync();

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await _cacheService.DistributeTenantModificationPreEventAsync(tenantId);
        await _systemRepositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);
        await tenantRepository.DeleteOneRtEntityAsync<RtSystemTenant>(systemSession,
            rtSystemTenant => rtSystemTenant.TenantId == octoTenant.TenantId.MakeKey());
        await _cacheService.DistributeTenantModificationPostEventAsync(tenantId);

    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsChildTenantExistingAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        return octoTenant != null;
    }

    public async Task<PagedResult<OctoTenant>> GetChildTenantsAsync(IOctoSession systemSession, int? skip = null,
        int? take = null)
    {
        var tenantRepository = await CreateCurrentTenantRepositoryAsync();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtSystemTenant>(systemSession, new DataQueryOperation(), skip, take);
        return new PagedResult<OctoTenant>(result.Result.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            skip, take, result.TotalCount);
    }

    public async Task<OctoTenant> GetChildTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var rtSystemTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId.MakeKey());
        if (rtSystemTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' not found.");
        }

        return new OctoTenant(rtSystemTenant.TenantId, rtSystemTenant.DatabaseName);
    }
    
    #endregion Tenant management

    public async Task UpdateTenantSystemCkModelAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await RestoreTenantSystemCkModelAsync(systemSession, octoTenant);
    }
    
    #region Access management
    
    // public async Task<ITenantContext> CreateChildTenantContextAsync(string tenantId)
    // {
    //     ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
    //     using var systemSession = await StartSystemSessionAsync();
    //     systemSession.StartTransaction();
    //
    //     var result = await CreateOrGetTenantRepositoryInternal(systemSession, tenantId);
    //     var context = new TenantContext(_systemConfiguration, tenantId, result.)
    //     
    //     await systemSession.CommitTransactionAsync();
    //     return result;
    // }

    public async Task<ITenantCkModelRepository> CreateTenantCkModelRepository(IOctoSession systemSession, string tenantId)
    {
        var databaseContext = await CreateDatabaseContextByTenantAsync(systemSession, tenantId);
        var tenantCkModelRepository = new TenantCkModelRepository(databaseContext);
        return tenantCkModelRepository;
    }

    public async Task<ITenantRepository> CreateOrGetTenantRepositoryAsync(string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        using var systemSession = await StartSystemSessionAsync();
        systemSession.StartTransaction();

        var result = await CreateOrGetTenantRepositoryInternal(systemSession, tenantId);

        await systemSession.CommitTransactionAsync();
        return result;
    }

    public async Task<ITenantRepositoryInternal> CreateOrGetTenantRepositoryInternalAsync(string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        using var systemSession = await StartSystemSessionAsync();
        systemSession.StartTransaction();

        var result = await CreateOrGetTenantRepositoryInternal(systemSession, tenantId);

        await systemSession.CommitTransactionAsync();
        return result;
    }
    
    #endregion Access management
    
    #region Configuration

    public async Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoSession systemSession, string key,
        TValueType defaultValue) where
        TValueType
        : struct
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        var o = await GetConfigAsync(systemSession, key, defaultValue);
        if (o == null)
        {
            return null;
        }

        return (TValueType)Convert.ChangeType(o, typeof(TValueType));
    }

    public async Task<string?> GetConfigurationAsync(IOctoSession systemSession, string key, string? defaultValue = null)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        return (string?)await GetConfigAsync(systemSession, key, defaultValue);
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
        var tenantRepository = await CreateCurrentTenantRepositoryAsync();

        var configuration = await tenantRepository.GetRtEntityByFilterAsync<RtSystemConfiguration>(systemSession,
            rtSystemConfiguration => rtSystemConfiguration.RtWellKnownName == key);
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

    
    private async Task<IDatabaseContext> CreateDatabaseContextByTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        return CreateDatabaseContext(octoTenant.DatabaseName);
    }

    internal protected async Task<ITenantRepositoryInternal> CreateCurrentTenantRepositoryAsync()
    {
        var databaseContext = CreateDatabaseContext(_databaseName);
        var tenantCkModelRepository = new TenantCkModelRepository(databaseContext);
        var ckCache = await _cacheService.GetOrCreateCkCacheAsync(TenantId, tenantCkModelRepository);
        return new TenantRepository(ckCache, databaseContext);
    }

    private IDatabaseContext CreateDatabaseContext(string databaseName)
    {
        return new DatabaseContext(_systemConfiguration.Value.DatabaseHost, databaseName,
            string.Format(_systemConfiguration.Value.DatabaseUser, databaseName),
            _systemConfiguration.Value.DatabaseUserPassword, _systemConfiguration.Value.AuthenticationDatabaseName,
            _systemConfiguration.Value.UseTls, _systemConfiguration.Value.AllowInsecureTls);
    }

    private async Task<ITenantRepositoryInternal> CreateOrGetTenantRepositoryInternal(IOctoSession systemSession,
        string tenantId)
    {
        try
        {
            await _semaphoreSlim.WaitAsync();

            var databaseContext = await CreateDatabaseContextByTenantAsync(systemSession, tenantId);
            var tenantCkModelRepository = await CreateTenantCkModelRepository(systemSession, tenantId);
            var ckCache = await _cacheService.GetOrCreateCkCacheAsync(tenantId, tenantCkModelRepository);
            return new TenantRepository(ckCache, databaseContext);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    private async Task RestoreTenantSystemCkModelAsync(IOctoSession systemSession, RtSystemTenant rtSystemTenant)
    {
        var ckModelRepository = await CreateTenantCkModelRepository(systemSession, rtSystemTenant.TenantId);
        await _ckSystemModelService.ImportAsync(systemSession, ckModelRepository);
        await SetConfigurationAsync(systemSession, Constants.SystemSchemaVersion, (object)Constants.SystemSchemaVersionValue);
    }


    private async Task<RtSystemTenant?> GetOctoDatabaseFromTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        var tenantRepository = await CreateCurrentTenantRepositoryAsync();
        return await tenantRepository.GetRtEntityByFilterAsync<RtSystemTenant>(systemSession, x => x.TenantId == tenantId.MakeKey());
    }

    protected async Task<bool> IsDatabaseAlreadyExistingAsync(string databaseName)
    {
        return await _systemRepositoryClient.IsRepositoryExistingAsync(databaseName);
    }

    private async Task<object?> GetConfigAsync(IOctoSession systemSession, string key, object? defaultValue)
    {
        var tenantRepository = await CreateCurrentTenantRepositoryAsync();
        var configuration = await tenantRepository.GetRtEntityByFilterAsync<RtSystemConfiguration>(systemSession,
            rtSystemConfiguration => rtSystemConfiguration.RtWellKnownName == key);
        if (configuration == null || configuration.ConfigurationValue == null)
        {
            return defaultValue;
        }

        return configuration.ConfigurationValue.Deserialize<object>();
    }

    #endregion Private methods
    
}