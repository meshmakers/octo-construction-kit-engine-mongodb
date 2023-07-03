using System;
using System.Threading.Tasks;
using CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Microsoft.Extensions.Options;
using NLog;
using Persistence.Contracts;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

// ReSharper disable once UnusedMember.Global
public class SystemContext : TenantContext, ISystemContextInternal
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SystemContext(IOptions<OctoSystemConfiguration> systemConfiguration,
        ICkCacheService ckCacheService, ICkSystemModelService ckSystemModelService)
    : base(systemConfiguration, systemConfiguration.Value.SystemTenantId, systemConfiguration.Value.SystemDatabaseName, ckSystemModelService, ckCacheService)
    {
    }

    #region System database handling

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateSystemDatabaseAsync()
    {
        if (await IsSystemDatabaseExistingAsync())
        {
            throw new DatabaseException("System database already exists.");
        }

        try
        {
            await _cacheService.DistributeTenantModificationPreEventAsync(_systemConfiguration.Value.SystemTenantId);
            
            await _systemRepositoryClient.CreateRepositoryAsync(_systemConfiguration.Value.SystemDatabaseName);

            using var systemSession = await StartSystemSessionAsync();
            systemSession.StartTransaction();

            var databaseContext = CreateSystemDatabaseContext();
            var ckModelRepository = new TenantCkModelRepository(databaseContext);

            await _ckSystemModelService.ImportAsync(systemSession, ckModelRepository);
            await SetConfigurationAsync(systemSession, Constants.SystemSchemaVersion, (object)Constants.SystemSchemaVersionValue);

            await systemSession.CommitTransactionAsync();
            
            await _cacheService.DistributeTenantModificationPostEventAsync(_systemConfiguration.Value.SystemTenantId);
        }
        catch (Exception)
        {
            await _systemRepositoryClient.DropRepositoryAsync(_systemConfiguration.Value.SystemDatabaseName);
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

        await DropSystemDatabaseAsync();
        await CreateSystemDatabaseAsync();
    }


    // ReSharper disable once UnusedMember.Global
    public async Task DropSystemDatabaseAsync()
    {
        if (!await IsSystemDatabaseExistingAsync())
        {
            throw new DatabaseException("System database does not exist.");
        }
        

        await _cacheService.DistributeTenantModificationPreEventAsync(_systemConfiguration.Value.SystemTenantId);
        await _systemRepositoryClient.DropRepositoryAsync(_systemConfiguration.Value.SystemDatabaseName);
        await _cacheService.DistributeTenantModificationPostEventAsync(_systemConfiguration.Value.SystemTenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsSystemDatabaseExistingAsync()
    {
        return await IsDatabaseAlreadyExistingAsync(_systemConfiguration.Value.SystemDatabaseName);
    }

    #endregion TenantId Context Handling

    #region Private methods
    

    // ReSharper disable once UnusedMember.Global
    private IDatabaseContext CreateSystemDatabaseContext()
    {
        return new DatabaseContext(_systemRepositoryClient, _systemConfiguration.Value.SystemDatabaseName);
    }

    #endregion Tenant handling
}