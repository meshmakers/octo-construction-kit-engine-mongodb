using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
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
            NormalizeDatabaseName(systemConfiguration.Value.SystemDatabaseName))
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

        var normalizedDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);
        var normalizedTenantId = _systemConfiguration.Value.SystemTenantId.NormalizeString();

        // IsSystemTenantExistingAsync above is false whenever the System CK model is missing or not
        // at the exact expected version - even when the database itself is present and full of data.
        // Bootstrapping over it used to run into the database-exists guard inside the try below, whose
        // catch then dropped the entire platform database (AB#4762). Refuse instead: the caller must
        // repair the CK model, not re-create the tenant. Deliberately explicit rather than generic -
        // this path has no untrusted caller and it fails host startup.
        // An infrastructure-only shell is exempt from the refusal (AB#4854): on a virgin server the
        // engine's own plumbing (lifecycle probe index, a setup-retry record from an earlier failed
        // attempt) can materialize the system database before this bootstrap runs, and refusing to
        // bootstrap over that shell wedged every fresh install — the datasource user was never created.
        var databaseExisted = await IsDatabaseExistingAsync(normalizedDatabaseName);
        if (databaseExisted && !await IsDatabaseMaterializedOnlyByInfrastructureAsync(normalizedDatabaseName))
        {
            throw TenantException.SystemTenantDatabaseNotBootstrappable(normalizedDatabaseName);
        }

        // Guards the destructive rollback below (mirrors CreateChildTenantAsync, AB#4762): it stays
        // false until we have provably created the database ourselves. Nothing that runs before it
        // is set may ever reach the drop — in particular a racing second replica whose
        // CreateTenantInternalAsync throws because the name was taken in the meantime.
        var databaseCreated = false;

        try
        {
            Guid correlationId = Guid.NewGuid();
            // Distribute updates (pre) to inform other services.
            await _tenantNotifications.NotifyPreTenantCreateAsync(normalizedTenantId, correlationId);

            // Create the database
            await CreateTenantInternalAsync(normalizedDatabaseName, allowInfrastructureMaterializedDatabase: true);
            databaseCreated = true;

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
            // Drop ONLY what this operation created from nothing (AB#4854): a pre-existing
            // infrastructure shell holds other services' durable bookkeeping (setup-retry queue,
            // lifecycle records, locks) and was possibly just fully bootstrapped by a racing
            // replica — the database of an attempt that started over an existing shell is kept.
            await CleanupFailedTenantCreationAsync(normalizedDatabaseName, normalizedTenantId,
                Guid.NewGuid(), e, dropDatabaseAndUser: databaseCreated && !databaseExisted);
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

        var normalizedDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);
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

    public async Task<bool> IsTenantRegisteredAsync(string tenantId)
    {
        if (tenantId.NormalizeString() == TenantId)
        {
            return await IsSystemTenantExistingAsync();
        }

        if (!await IsSystemTenantExistingAsync())
        {
            // Without the system tenant the registry is unreadable; report unregistered so callers
            // treat this like the quiet bootstrap skip the setup path already performs.
            return false;
        }

        // Pure registry read — deliberately NOT TryGetChildTenantContextAsync, whose resolve runs the
        // CK model auto-imports; this probe gates per-CK-import events and must stay cheap (AB#4829).
        using var session = await GetAdminSessionAsync();
        session.StartTransaction();
        var exists = await IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return exists;
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
        var normalizedDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);

        if (await IsDatabaseExistingAsync(normalizedDatabaseName))
        {
            // An infrastructure-only shell is not an existing system tenant (AB#4854). The check must
            // run BEFORE the CK model read below: that read uses the datasource-user connection, and
            // on a shell that user does not exist yet — the read then fails the whole probe with an
            // authentication error instead of answering "false", wedging every caller at startup.
            if (await IsDatabaseMaterializedOnlyByInfrastructureAsync(normalizedDatabaseName))
            {
                return false;
            }

            if (await IsCkModelExistingAsync(SystemCkIds.CkModelId))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IResultSet<OctoTenant>> GetAllTenantsAsync(IOctoAdminSession adminSession, int? skip = null,
        int? take = null)
    {
        var tenantRepository = GetTenantRepositoryAsAdmin();

        // Deliberately unfiltered: the system database doubles as the platform-wide routing registry,
        // so this returns every tenant of the installation regardless of its logical parent. This is
        // the enumeration for installation-wide concerns (token cleanup, CORS, health checks,
        // observability); GetChildTenantsAsync returns direct children only (AB#5025).
        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtTenant>(adminSession,
            RtEntityQueryOptions.Create(), skip, take);
        return new ResultSet<OctoTenant>(result.Items.Select(d => new OctoTenant(d.TenantId, d.DatabaseName)),
            result.TotalCount, null, null);
    }

    /// <inheritdoc />
    public async Task<bool> IsSystemDatabaseBootstrappableAsync()
    {
        var normalizedDatabaseName = NormalizeDatabaseName(_systemConfiguration.Value.SystemDatabaseName);

        return !await IsDatabaseExistingAsync(normalizedDatabaseName)
               || await IsDatabaseMaterializedOnlyByInfrastructureAsync(normalizedDatabaseName);
    }

    #endregion TenantId Context Handling

    #region Construction Kit Model Handling

    public Task EnsureSystemCkModelAsync()
    {
        // The infrastructure-shell guard (AB#4854) lives inside UpdateSystemCkModelAsync, at the
        // seed decision itself. A guard here would be check-then-act: a shell that materializes
        // between this method's probe and the seed would still be seeded, re-creating the wedge.
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

    Task<bool> ISystemContext.IsDatabaseExistingAsync(string databaseName)
    {
        // Bridges the protected TenantContext helper onto the interface so consumers
        // (e.g. TenantBackupService post-restore verification) can check database existence.
        return IsDatabaseExistingAsync(databaseName);
    }

    /// <inheritdoc />
    public async Task<string?> TryGetTenantIdByDatabaseNameAsync(string databaseName)
    {
        // Commit on success only — a commit in a finally block would run after a failed read and
        // its own failure would mask the original exception. Disposing the session aborts an
        // uncommitted transaction, matching the read-session pattern used elsewhere.
        using var session = await GetAdminSessionAsync();
        session.StartTransaction();
        var owner = await GetRtSystemTenantByDatabaseNameAsync(session, NormalizeDatabaseName(databaseName));
        await session.CommitTransactionAsync();
        return owner?.TenantId;
    }

    #endregion
}
