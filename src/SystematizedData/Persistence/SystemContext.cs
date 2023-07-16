using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.Common.Shared.DistributedCache;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.SystematizedData.Persistence;

// ReSharper disable once UnusedMember.Global
public class SystemContext : ISystemContext
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<string, ICkCache?> _ckCaches;
    private readonly ICachedCollection<OctoConfiguration> _configurationCollection;

    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly IRepositoryClient _repositoryClient;
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    private readonly OctoSystemConfiguration _systemConfiguration;

    private readonly ICachedCollection<SystemEntities.OctoTenant> _tenantCollection;

    public SystemContext(IOptions<OctoSystemConfiguration> systemConfiguration,
        IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _systemConfiguration = systemConfiguration.Value;
        _distributedWithPubSubCache = distributedWithPubSubCache;

        var sharedSettings = new MongoConnectionOptions
        {
            MongoDbHost = _systemConfiguration.DatabaseHost,
            MongoDbUsername = _systemConfiguration.AdminUser,
            MongoDbPassword = _systemConfiguration.AdminUserPassword,
            AuthenticationSource = _systemConfiguration.AuthenticationDatabaseName,
            UseTls = _systemConfiguration.UseTls,
            AllowInsecureTls = _systemConfiguration.AllowInsecureTls
        };

        _ckCaches = new ConcurrentDictionary<string, ICkCache?>();

        _repositoryClient = new MongoRepositoryClient(sharedSettings);
        OctoSystemDatabase = _repositoryClient.GetRepository(_systemConfiguration.SystemDatabaseName);

        _tenantCollection = OctoSystemDatabase.GetCollection<SystemEntities.OctoTenant>();
        _configurationCollection = OctoSystemDatabase.GetCollection<OctoConfiguration>();

        var sub = _distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyTenantUpdate);
        sub.OnMessage(channelMessage =>
        {
            if (!string.IsNullOrWhiteSpace(channelMessage.Message))
            {
                RemoveCkCache(channelMessage.Message);
            }
            return Task.CompletedTask;
        });
    }

    #region Transaction handling

    public async Task<IOctoSession> StartSystemSessionAsync()
    {
        var systemSession = await OctoSystemDatabase.StartSessionAsync();
        return systemSession;
    }

    #endregion Transaction handling

    #region System database handling

    public IRepository OctoSystemDatabase { get; }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateSystemDatabaseAsync()
    {
        if (await IsSystemDatabaseExistingAsync())
        {
            throw new DatabaseException("System database already exists.");
        }

        try
        {
            await _repositoryClient.CreateRepositoryAsync(_systemConfiguration.SystemDatabaseName);

            using var systemSession = await OctoSystemDatabase.StartSessionAsync();
            systemSession.StartTransaction();

            await CreateSystemSchemaAsync(systemSession);

            UnloadAllCaches();

            await systemSession.CommitTransactionAsync();
        }
        catch (Exception)
        {
            await _repositoryClient.DropRepositoryAsync(_systemConfiguration.SystemDatabaseName);
            throw;
        }
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearSystemDatabaseAsync()
    {
        if (!await IsSystemDatabaseExistingAsync())
        {
            throw new DatabaseException("System database does not exist.");
        }

        await CreateSystemDatabaseAsync();
    }

    public async Task UpdateSystemSchemaAsync(IOctoSession systemSession)
    {
        if (!await IsSystemDatabaseExistingAsync())
        {
            throw new DatabaseException("System database does not exist.");
        }

        var version = await GetConfigurationAsync(systemSession, Constants.SystemSchemaVersion, 0);

        if (version < Constants.SystemSchemaVersionValue)
        {
            await CreateSystemSchemaAsync(systemSession);
        }
    }

    private async Task CreateSystemSchemaAsync(IOctoSession systemSession)
    {
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<SystemEntities.OctoTenant>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoConfiguration>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoUser>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoRole>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoPermission>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoPermissionRole>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoClient>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoApiResource>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoApiScope>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoIdentityResource>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoPersistedGrant>(false);
        await OctoSystemDatabase.CreateCollectionIfNotExistsAsync<OctoIdentityProvider>(false);

        await SetConfigAsync(systemSession, Constants.SystemSchemaVersion, Constants.SystemSchemaVersionValue);
    }

    // ReSharper disable once UnusedMember.Global
    public async Task DropSystemDatabaseAsync()
    {
        if (!await IsSystemDatabaseExistingAsync())
        {
            throw new DatabaseException("System database does not exist.");
        }

        await _repositoryClient.DropRepositoryAsync(_systemConfiguration.SystemDatabaseName);

        UnloadAllCaches();
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsSystemDatabaseExistingAsync()
    {
        return await IsDatabaseAlreadyExistingAsync(_systemConfiguration.SystemDatabaseName);
    }

    #endregion System database handling


    #region TenantId Context Handling

    public async Task<ITenantContext> CreateOrGetTenantContextAsync(string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        using var systemSession = await StartSystemSessionAsync();
        systemSession.StartTransaction();

        var result = await CreateOrGetTenantContextInternal(systemSession, tenantId);

        await systemSession.CommitTransactionAsync();
        return result;
    }

    public async Task ClearCacheAsync(string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        
        await UnloadTenantCachesAsync(tenantId);
    }

    private async Task<ITenantContextInternal> CreateOrGetTenantContextInternal(IOctoSession systemSession,
        string tenantId)
    {
        if (TryGetCkCache(tenantId, out var ckCache))
        {
            return await CreateTenantContextAsync(systemSession, ckCache!);
        }

        try
        {
            await _semaphoreSlim.WaitAsync();

            if (TryGetCkCache(tenantId, out ckCache))
            {
                return await CreateTenantContextAsync(systemSession, ckCache!);
            }

            var databaseContext = await CreateDatabaseContextByTenantAsync(systemSession, tenantId);
            ckCache = new CkCache(tenantId);
            await ckCache.Initialize(databaseContext);

            var key = tenantId.MakeKey();
            _ckCaches[key] = ckCache;
            return await CreateTenantContextAsync(systemSession, ckCache);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    private async Task<ITenantContextInternal> CreateTenantContextAsync(IOctoSession systemSession,
        ICkCache ckCache)
    {
        var databaseContext = await CreateDatabaseContextByTenantAsync(systemSession, ckCache.TenantId);
        var tenantRepository = new TenantRepository(ckCache, databaseContext);
        return new TenantContext(ckCache.TenantId, tenantRepository, ckCache);
    }

    public bool TryGetCkCache(string tenantId, out ICkCache? ckCache)
    {
        var key = tenantId.MakeKey();

        if (_ckCaches.TryGetValue(key, out ckCache))
        {
            if (ckCache != null && !ckCache.IsDisposed)
            {
                return true;
            }
        }

        return false;
    }

    #endregion TenantId Context Handling

    #region User Data Source handling

    private async Task<SystemEntities.OctoTenant?> GetOctoDatabaseFromTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        return await _tenantCollection.DocumentAsync(systemSession, tenantId.MakeKey());
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsTenantExistingAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        return octoTenant != null;
    }

    private async Task<bool> IsDatabaseAlreadyExistingAsync(string databaseName)
    {
        return await _repositoryClient.IsRepositoryExistingAsync(databaseName);
    }

    public async Task<PagedResult<OctoTenant>> GetTenantsAsync(IOctoSession systemSession, int? skip = null,
        int? take = null)
    {
        var result = await _tenantCollection.GetAsync(systemSession, skip, take);
        var totalCount = await _tenantCollection.GetTotalCountAsync(systemSession);
        return new PagedResult<OctoTenant>(result.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            skip, take, totalCount);
    }

    public async Task<OctoTenant> GetTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await _tenantCollection.DocumentAsync(systemSession, tenantId.MakeKey());
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' not found.");
        }

        return new OctoTenant(octoTenant.TenantId, octoTenant.DatabaseName);
    }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateTenantAsync(IOctoSession systemSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var normalizedDatabaseName = databaseName.ToLower();
        var normalizedTenantId = tenantId.MakeKey();
        if (await IsTenantExistingAsync(systemSession, normalizedTenantId))
        {
            throw new TenantException($"Tenant '{normalizedTenantId}' already exists.");
        }

        if (await IsDatabaseAlreadyExistingAsync(normalizedDatabaseName))
        {
            throw new DatabaseException($"Database '{normalizedDatabaseName}' already exists.");
        }

        await _repositoryClient.CreateRepositoryAsync(normalizedDatabaseName);
        await _repositoryClient.CreateUser(systemSession, _systemConfiguration.AuthenticationDatabaseName,
            normalizedDatabaseName, string.Format(_systemConfiguration.DatabaseUser, normalizedDatabaseName),
            _systemConfiguration.DatabaseUserPassword);

        var repository = _repositoryClient.GetRepository(normalizedDatabaseName);
        await repository.CreateCollectionIfNotExistsAsync<CkAttribute>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkEntity>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkEntityAssociation>(false);
        await repository.CreateCollectionIfNotExistsAsync<CkEntityInheritance>(false);
        await repository.CreateCollectionIfNotExistsAsync<RtAssociation>(true);


        var octoTenant = new SystemEntities.OctoTenant
        {
            TenantId = normalizedTenantId,
            DatabaseName = normalizedDatabaseName
        };

        await _tenantCollection.InsertAsync(systemSession, octoTenant);
        await RestoreTenantSystemCkModelAsync(systemSession, octoTenant);
    }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global
    public async Task AttachTenantAsync(IOctoSession systemSession, string databaseName, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        if (await IsTenantExistingAsync(systemSession, tenantId))
        {
            throw new TenantException($"Tenant '{tenantId}' already exists.");
        }

        if (!await IsDatabaseAlreadyExistingAsync(databaseName))
        {
            throw new DatabaseException($"Database '{databaseName}' does not exist.");
        }

        var octoTenant = new SystemEntities.OctoTenant
        {
            TenantId = tenantId,
            DatabaseName = databaseName
        };

        await _tenantCollection.InsertAsync(systemSession, octoTenant);
    }

    public async Task DetachTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exists.");
        }

        await _tenantCollection.DeleteOneAsync(systemSession, octoTenant.TenantId);
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await DropTenantAsync(systemSession, tenantId);
        await CreateTenantAsync(systemSession, octoTenant.DatabaseName, tenantId);
        await UnloadTenantCachesAsync(tenantId);
    }

    private async Task<IDatabaseContext> CreateDatabaseContextByTenantAsync(IOctoSession systemSession,
        string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        return new DatabaseContext(_systemConfiguration.DatabaseHost, octoTenant.DatabaseName,
            string.Format(_systemConfiguration.DatabaseUser, octoTenant.DatabaseName),
            _systemConfiguration.DatabaseUserPassword, _systemConfiguration.AuthenticationDatabaseName,
            _systemConfiguration.UseTls, _systemConfiguration.AllowInsecureTls);
    }

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

    private async Task RestoreTenantSystemCkModelAsync(IOctoSession systemSession, SystemEntities.OctoTenant octoTenant)
    {
        var ckModelFilePath = Path.Combine(Helper.AssemblyDirectory, "CKModel.json");
        Logger.Info("Importing construction kit model '{CkModelFilePath}'", ckModelFilePath);
        await ImportCkModelAsync(systemSession, octoTenant.TenantId, ScopeIds.System, ckModelFilePath, null);
        Logger.Info("Construction kit model imported.");
    }

// ReSharper disable once MemberCanBePrivate.Global
    public async Task DropTenantAsync(IOctoSession systemSession, string tenantId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var octoTenant = await GetOctoDatabaseFromTenantAsync(systemSession, tenantId);
        if (octoTenant == null)
        {
            throw new TenantException($"Tenant '{tenantId}' does not exist.");
        }

        await UnloadTenantCachesAsync(tenantId);
        await _repositoryClient.DropRepositoryAsync(octoTenant.DatabaseName);
        await _tenantCollection.DeleteOneAsync(systemSession, octoTenant.TenantId);
    }

    #endregion Tenant handling

    #region Model handling

// ReSharper disable once UnusedMember.Global
    public async Task ImportCkModelAsTextAsync(IOctoSession systemSession, string tenantId, ScopeIds scopeId,
        string jsonText)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(jsonText), jsonText);

        var databaseContext = await CreateDatabaseContextByTenantAsync(systemSession, tenantId);
        using var session = await databaseContext.StartSessionAsync();
        session.StartTransaction();

        var importer = new ImportCkModel(databaseContext);
        await importer.ImportText(session, jsonText, scopeId);

        await session.CommitTransactionAsync();

        await UnloadTenantCachesAsync(tenantId);
    }

    public async Task ImportCkModelAsync(IOctoSession systemSession, string tenantId, ScopeIds scopeId,
        string filePath, CancellationToken? cancellationToken)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateExistingFile(nameof(filePath), filePath);

        var databaseContext = await CreateDatabaseContextByTenantAsync(systemSession, tenantId);
        using var session = await databaseContext.StartSessionAsync();
        session.StartTransaction();

        var importer = new ImportCkModel(databaseContext);
        await importer.Import(session, filePath, scopeId, cancellationToken);

        await session.CommitTransactionAsync();

        await UnloadTenantCachesAsync(tenantId);
    }

    #endregion Model handling

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
        await SetConfigAsync(systemSession, key, value);
    }

    public async Task SetConfigurationAsync(IOctoSession systemSession, string key, string value)
    {
        ArgumentValidation.ValidateString(nameof(key), key);
        await SetConfigAsync(systemSession, key, value);
    }


    private async Task<object?> GetConfigAsync(IOctoSession systemSession, string key, object? defaultValue)
    {
        var document = await _configurationCollection.DocumentAsync(systemSession, key);
        if (document == null)
        {
            return defaultValue;
        }

        return document.Value;
    }

    public async Task SetConfigAsync(IOctoSession systemSession, string key, object value)
    {
        var document = await _configurationCollection.DocumentAsync(systemSession, key);
        if (document == null)
        {
            document = new OctoConfiguration { Key = key, Value = value };
            await _configurationCollection.InsertAsync(systemSession, document);
        }

        else
        {
            document.Value = value;
            await _configurationCollection.ReplaceByIdAsync(systemSession, key, document);
        }
    }

    #endregion Configuration

    #region Private Methods

    private async Task UnloadTenantCachesAsync(string tenantId)
    {
        await _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyTenantUpdate, tenantId);
        RemoveCkCache(tenantId);
    }

    private void RemoveCkCache(string tenantId)
    {
        if (_ckCaches.TryRemove(tenantId, out var ckCache))
        {
            ckCache?.Dispose();
        }
    }

    private void UnloadAllCaches()
    {
        _ckCaches.Clear();
    }

    #endregion Private Methods
}
