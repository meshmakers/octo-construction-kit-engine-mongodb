using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

// ReSharper disable once UnusedMember.Global
public class SystemContext : TenantContext, ISystemContext
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="SystemContext" /> class.
    /// </summary>
    /// <param name="loggerFactory"></param>
    /// <param name="systemConfiguration"></param>
    /// <param name="serviceProvider"></param>
    public SystemContext(ILoggerFactory loggerFactory,
        IOptions<OctoSystemConfiguration> systemConfiguration,
        IServiceProvider serviceProvider)
        : base(loggerFactory, systemConfiguration, serviceProvider,
            systemConfiguration.Value.SystemTenantId.NormalizeString(),
            systemConfiguration.Value.SystemDatabaseName.ToLower())
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

        var normalizedDatabaseName = SystemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = SystemConfiguration.Value.SystemTenantId.NormalizeString();

        try
        {
            Guid correlationId = Guid.NewGuid();
            // Distribute updates (pre) to inform other services.
            await TenantNotifications.NotifyPreTenantCreateAsync(normalizedTenantId, correlationId);

            // Create database
            await CreateTenantInternalAsync(normalizedDatabaseName);

            // Restore the tenant system model on the newly created repository
            var ckModelRepository = CreateRepositoryDataSourceAsAdmin(normalizedDatabaseName);
            OperationResult operationResult = new();
            var ckCompiledModelRoot =
                await CkModelRepositoryService.LookupCkModelAsync(SystemCkIds.ModelId, operationResult);
            if (ckCompiledModelRoot == null)
            {
                throw TenantException.SystemModelNotFound();
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw TenantException.ErrorDuringSystemModelLoad(operationResult);
            }

            await CkModelRepositoryService.PublishModelAsync(InternalConstants.CkModelRepositoryName,
                ckCompiledModelRoot, true, false,
                new TenantDatabaseSourceIdentifier(ckModelRepository));
            // Distribute updates (post) to inform other services.
            await TenantNotifications.NotifyPosTenantCreateAsync(normalizedTenantId, correlationId);
        }
        catch (Exception e)
        {
            throw TenantException.CreateSystemTenantFailed(e);
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

        var normalizedDatabaseName = SystemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = SystemConfiguration.Value.SystemTenantId.NormalizeString();
        Guid correlationId = Guid.NewGuid();

        try
        {
            await TenantNotifications.NotifyPreTenantDeleteAsync(normalizedTenantId, correlationId);
            await AdminRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
        }
        catch (MongoCommandException)
        {
            throw TenantException.DeleteSystemTenantFailed();
        }
        finally
        {
            await TenantNotifications.NotifyPosTenantDeleteAsync(normalizedTenantId, correlationId);
        }
    }

    public async Task<ITenantContext> FindTenantContextAsync(string tenantId)
    {
        var tenantContext = await TryFindTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            throw TenantException.TenantDoesNotExist(tenantId);
        }
        return tenantContext;
    }

    public async Task<ITenantContext?> TryFindTenantContextAsync(string tenantId)
    {
        ITenantContext tenantContext = this;
        if (tenantId.NormalizeString() != TenantId)
        {
            var childTenantContext = await TryGetChildTenantContextAsync(tenantId);
            if (childTenantContext == null)
            {
                return null;
            }
            tenantContext = childTenantContext;
        }

        return tenantContext;
    }


    public async Task<ITenantRepository> FindTenantRepositoryAsync(string tenantId)
    {
        var tenantContext = await FindTenantContextAsync(tenantId);
        return tenantContext.GetTenantRepository();
    }

    public async Task<ITenantRepository?> TryFindTenantRepositoryAsync(string tenantId)
    {
        var tenantContext = await TryFindTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            return null;
        }
        return tenantContext.GetTenantRepository();
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public async Task<bool> IsSystemTenantExistingAsync()
    {
        var normalizedDatabaseName = SystemConfiguration.Value.SystemDatabaseName.ToLower();

        if (await IsDatabaseExistingAsync(normalizedDatabaseName))
        {
            if (await IsCkModelExistingAsync(SystemCkIds.ModelId))
            {
                return true;
            }
        }

        return false;
    }

    #endregion TenantId Context Handling
}
