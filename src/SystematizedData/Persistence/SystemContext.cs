using System;
using System.Threading.Tasks;
using CkModel.CkRuleEngine;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Microsoft.Extensions.Options;
using NLog;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

// ReSharper disable once UnusedMember.Global
public class SystemContext : TenantContext, ISystemContextInternal
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SystemContext(IOptions<OctoSystemConfiguration> systemConfiguration,
        ICkCacheService ckCacheService, ICkSystemModelService ckSystemModelService)
    : base(systemConfiguration, systemConfiguration.Value.SystemTenantId, systemConfiguration.Value.SystemDatabaseName.ToLower(), ckSystemModelService, ckCacheService)
    {
    }

    #region System database handling

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateSystemTenantAsync()
    {
        if (await IsSystemTenantExistingAsync())
        {
            throw new DatabaseException("System database already exists.");
        }

        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.MakeKey();

        try
        {
            // Distribute updates (post) to inform other services.
            await _cacheService.DistributeTenantModificationPreEventAsync(normalizedTenantId);

            using var systemSession = await StartSystemSessionAsync();
            systemSession.StartTransaction();
            
            // Create database
            await CreateTenantInternalAsync(normalizedDatabaseName);

            // Restore the tenant system model on the newly created repository
            var ckModelRepository = CreateTenantCkModelRepository();
            await _ckSystemModelService.ImportAsync(systemSession, ckModelRepository);

            await systemSession.CommitTransactionAsync();
            
            // Distribute updates (post) to inform other services.
            await _cacheService.DistributeTenantModificationPostEventAsync(normalizedTenantId);
        }
        catch (Exception)
        {
            await _systemRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            throw;
        }
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearSystemTenantAsync()
    {
        if (!await IsSystemTenantExistingAsync())
        {
            throw new DatabaseException("System database does not exist.");
        }

        await DeleteSystemTenantAsync();
        await CreateSystemTenantAsync();
    }


    // ReSharper disable once UnusedMember.Global
    public async Task DeleteSystemTenantAsync()
    {
        if (!await IsSystemTenantExistingAsync())
        {
            throw new DatabaseException("System database does not exist.");
        }
        

        await _cacheService.DistributeTenantModificationPreEventAsync(_systemConfiguration.Value.SystemTenantId);
        await _systemRepositoryClient.DropRepositoryAsync(_systemConfiguration.Value.SystemDatabaseName);
        await _cacheService.DistributeTenantModificationPostEventAsync(_systemConfiguration.Value.SystemTenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsSystemTenantExistingAsync()
    {
        return await IsDatabaseAlreadyExistingAsync(_systemConfiguration.Value.SystemDatabaseName);
    }

    #endregion TenantId Context Handling
}