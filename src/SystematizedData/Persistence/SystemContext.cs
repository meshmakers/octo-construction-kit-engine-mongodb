using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Persistence.InternalContracts;
using Persistence.SystemCkModel.ConstructionKit.Generated.System.v1;

namespace Meshmakers.Octo.SystematizedData.Persistence;

// ReSharper disable once UnusedMember.Global
public class SystemContext : TenantContext, ISystemContextInternal
{
    public SystemContext(ILoggerFactory loggerFactory, IOptions<OctoSystemConfiguration> systemConfiguration,
        ISystemMessageService systemMessageService,
        ICkCacheService ckCacheService, ICkModelRepositoryService ckModelRepositoryService, IModelLoaderService modelLoaderService)
    : base(loggerFactory, systemConfiguration, systemConfiguration.Value.SystemTenantId, systemConfiguration.Value.SystemDatabaseName.ToLower(), 
        systemMessageService, ckModelRepositoryService, ckCacheService, modelLoaderService)
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
            await _systemMessageService.DistributeTenantModificationPreEventAsync(normalizedTenantId);

            using var systemSession = await StartSystemSessionAsync();
            systemSession.StartTransaction();
            
            // Create database
            await CreateTenantInternalAsync(normalizedDatabaseName);

            // Restore the tenant system model on the newly created repository
            var ckModelRepository = CreateDatabaseContext(normalizedDatabaseName);
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
                new TenantDatabaseSourceIdentifier(ckModelRepository, systemSession));
            await systemSession.CommitTransactionAsync();
            
            // Distribute updates (post) to inform other services.
            await _systemMessageService.DistributeTenantModificationPostEventAsync(normalizedTenantId);
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
        
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.MakeKey();

        await _systemMessageService.DistributeTenantModificationPreEventAsync(normalizedTenantId);
        await _systemRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
        await _systemMessageService.DistributeTenantModificationPostEventAsync(normalizedTenantId);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsSystemTenantExistingAsync()
    {
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();

        return await IsDatabaseAlreadyExistingAsync(normalizedDatabaseName);
    }

    #endregion TenantId Context Handling
}