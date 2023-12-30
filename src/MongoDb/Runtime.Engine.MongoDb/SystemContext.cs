using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Persistence.SystemCkModel.ConstructionKit.Generated.System.v1;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

// ReSharper disable once UnusedMember.Global
public class SystemContext : TenantContext, ISystemContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SystemContext"/> class.
    /// </summary>
    /// <param name="loggerFactory"></param>
    /// <param name="systemConfiguration"></param>
    /// <param name="tenantNotifications"></param>
    /// <param name="ckCacheService"></param>
    /// <param name="ckModelRepositoryService"></param>
    /// <param name="modelLoaderService"></param>
    /// <param name="bulkRtMutation"></param>
    public SystemContext(ILoggerFactory loggerFactory, IOptions<OctoSystemConfiguration> systemConfiguration,
        ITenantNotifications tenantNotifications,
        ICkCacheService ckCacheService, ICkModelRepositoryService ckModelRepositoryService, IModelLoaderService modelLoaderService,
        IBulkRtMutation bulkRtMutation)
        : base(loggerFactory, systemConfiguration, systemConfiguration.Value.SystemTenantId.MakeKey(),
            systemConfiguration.Value.SystemDatabaseName.ToLower(),
            tenantNotifications, ckModelRepositoryService, ckCacheService, modelLoaderService, bulkRtMutation)
    {
    }

    #region System database handling

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task CreateSystemTenantAsync()
    {
        if (await IsSystemTenantExistingAsync())
        {
            throw TenantException.SystemTenantAlreadyExisting();
        }

        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.MakeKey();

        try
        {
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(normalizedTenantId);

            using var systemSession = await GetSystemSessionAsync();
            systemSession.StartTransaction();

            // Create database
            await CreateTenantInternalAsync(normalizedDatabaseName);

            // Restore the tenant system model on the newly created repository
            var ckModelRepository = CreateDatabaseContext(normalizedDatabaseName);
            OperationResult operationResult = new();
            var ckCompiledModelRoot = await _ckModelRepositoryService.LookupCkModelAsync(SystemCkIds.ModelId, operationResult);
            if (ckCompiledModelRoot == null) throw TenantException.SystemModelNotFound();

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
                throw TenantException.ErrorDuringSystemModelLoad(operationResult);

            await _ckModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName, ckCompiledModelRoot, true,
                new TenantDatabaseSourceIdentifier(ckModelRepository, systemSession));
            await systemSession.CommitTransactionAsync();

           
        }
        catch (Exception)
        {
            await _systemRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
            throw;
        }
        finally
        {
            // Distribute updates (post) to inform other services.
            await _tenantNotifications.NotifyPosTenantCreateAsync(normalizedTenantId);
        }
    }

    // ReSharper disable once UnusedMember.Global
    public async Task ClearSystemTenantAsync()
    {
        if (!await IsSystemTenantExistingAsync())
        {
            throw TenantException.SystemTenantDatabaseNotExisting();
        }

        await DeleteSystemTenantAsync();
        await CreateSystemTenantAsync();
    }


    // ReSharper disable once UnusedMember.Global
    public async Task DeleteSystemTenantAsync()
    {
        if (!await IsSystemTenantExistingAsync())
        {
            throw TenantException.SystemTenantDatabaseNotExisting();
        }

        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.MakeKey();

        try
        {
            await _tenantNotifications.NotifyPreTenantDeleteAsync(normalizedTenantId);
            await _systemRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantDeleteAsync(normalizedTenantId);
        }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsSystemTenantExistingAsync()
    {
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();

        return await IsDatabaseAlreadyExistingAsync(normalizedDatabaseName);
    }

    #endregion TenantId Context Handling
}