using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

// ReSharper disable once UnusedMember.Global
public class SystemContext : TenantContext, ISystemContext
{
    private readonly IServiceProvider _serviceProvider;

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
        _serviceProvider = serviceProvider;
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
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();

        try
        {
            Guid correlationId = Guid.NewGuid();
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(normalizedTenantId, correlationId);

            // Create the database
            await CreateTenantInternalAsync(normalizedDatabaseName);

            // Restore the tenant system model on the newly created repository
            var ckModelRepository = CreateRepositoryDataSourceAsAdmin(normalizedDatabaseName, normalizedTenantId);

            OperationResult operationResult = new();
            var ckCompiledModelRoot =
                await _catalogService.GetAsync(SystemCkIds.CkModelId, operationResult);
            if (ckCompiledModelRoot == null)
            {
                throw TenantException.SystemModelNotFoundInCatalog(SystemCkIds.CkModelId);
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw TenantException.ErrorDuringSystemModelLoad(operationResult);
            }

            await _ckModelRepositoryService.UpdateModelAsync(ckCompiledModelRoot,
                new TenantDatabaseSourceIdentifier(null, ckModelRepository, normalizedTenantId));

            // Distribute updates (post) to inform other services.
            await _tenantNotifications.NotifyPosTenantCreateAsync(normalizedTenantId, correlationId);
        }
        catch (Exception e)
        {
            // Roll back the (partially) created system database + user before surfacing the failure
            // (AB#1958). The event-log write is a no-op while the system tenant itself does not yet
            // exist, but the database/user rollback prevents a half-created system tenant.
            await CleanupFailedTenantCreationAsync(normalizedDatabaseName, normalizedTenantId,
                Guid.NewGuid(), e, dropDatabaseAndUser: true);
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

        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();
        Guid correlationId = Guid.NewGuid();

        try
        {
            await _tenantNotifications.NotifyPreTenantDeleteAsync(normalizedTenantId, correlationId);
            await _adminRepositoryClient.DropRepositoryAsync(normalizedDatabaseName);
        }
        catch (MongoCommandException)
        {
            throw TenantException.DeleteSystemTenantFailed();
        }
        finally
        {
            await _tenantNotifications.NotifyPosTenantDeleteAsync(normalizedTenantId, correlationId);
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
        if (!await IsSystemTenantExistingAsync())
        {
            throw TenantException.SystemTenantDatabaseNotExisting();
        }

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
        else
        {
            // The system tenant resolves to `this` and bypasses TryGetChildTenantContextAsync, so the
            // service-managed descriptor import (e.g. System.UI into octosystem) must fire here too.
            await EnsureServiceManagedCkModelsImportedAsync();
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
        var normalizedDatabaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();

        if (await IsDatabaseExistingAsync(normalizedDatabaseName))
        {
            if (await IsCkModelExistingAsync(SystemCkIds.CkModelId))
            {
                return true;
            }
        }

        return false;
    }

    #endregion TenantId Context Handling

    #region Construction Kit Model Handling

    public Task EnsureSystemCkModelAsync()
    {
        return UpdateSystemCkModelAsync(DatabaseName, TenantId);
    }

    #endregion Construction Kit Model Handling

    #region Backup and Restore

    public Task<CommandResult> BackupTenantAsync(string tenantId, string archiveFilePath,
        bool detachTenant = false, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var backupService = _serviceProvider.GetRequiredService<ITenantBackupService>();
        return backupService.BackupTenantAsync(tenantId, archiveFilePath, detachTenant, timeout, cancellationToken);
    }

    public Task<CommandResult> RestoreTenantAsync(string tenantId, string databaseName, string archiveFilePath,
        string? sourceDatabaseName = null, bool dropExistingTenant = true, bool attachTenant = true,
        TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var backupService = _serviceProvider.GetRequiredService<ITenantBackupService>();
        return backupService.RestoreTenantAsync(tenantId, databaseName, archiveFilePath, sourceDatabaseName,
            dropExistingTenant, attachTenant, timeout, cancellationToken);
    }

    public Task<CommandResult> CloneTenantToTempAsync(string sourceTenantId, string tempTenantId,
        string tempDatabaseName, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var backupService = _serviceProvider.GetRequiredService<ITenantBackupService>();
        return backupService.CloneTenantToTempAsync(sourceTenantId, tempTenantId, tempDatabaseName,
            timeout, cancellationToken);
    }

    #endregion
}
