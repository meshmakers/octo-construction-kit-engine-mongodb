using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A tenant restore must never overwrite a database that belongs to another OctoMesh instance on the
///     same MongoDB server (AB#8672). Instance A is the fixture, instance B its
///     <see cref="TwoInstanceFixture.Other" /> with its own system database on the same server.
/// </summary>
[Collection(TwoInstanceCollection.Name)]
public class RestoreOwnershipGuardTests(TwoInstanceFixture fixture)
{
    private ISystemContext InstanceA => fixture.GetSystemContext();

    private ISystemContext InstanceB => fixture.Other.GetSystemContext();

    private string SystemDatabaseA => fixture.SystemDatabaseName.ToLowerInvariant();

    private string SystemDatabaseB => fixture.Other.SystemDatabaseName.ToLowerInvariant();

    private TenantOwnershipStore OwnershipStoreA => fixture.GetService<TenantOwnershipStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static string NewArchivePath() => Path.Combine(Path.GetTempPath(), $"rstguard_{Guid.NewGuid():N}.gz");

    [Fact]
    public async Task Restore_IntoTenantDatabaseOfAnotherInstance_IsRefusedNamingTheOwnerInTheLog_AndKeepsItsData()
    {
        var sourceTenantId = NewName("rgsrc");
        var foreignTenantId = NewName("rgforeign");
        var restoredTenantId = NewName("rgtarget");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceB, foreignTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(restoredTenantId, foreignTenantId, archivePath);

            RestoreRefusalAssert.IsUniformAndLogged(result, fixture.Logs, restoredTenantId, foreignTenantId,
                "another OctoMesh instance", SystemDatabaseB, $"tenant '{foreignTenantId}'");

            // Nothing was dropped or attached: B still owns and resolves its tenant.
            var marker = await OwnershipStoreA.GetAsync(foreignTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseB, marker!.OwnerSystemDatabaseName);
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(foreignTenantId));
            Assert.Null(await InstanceA.TryFindTenantContextAsync(restoredTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, foreignTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoUnmarkedTenantDatabaseRegisteredByAnotherInstance_IsRefusedNamingTheOwnerInTheLog()
    {
        // A tenant of B from before the ownership marker shipped: only B's registry says it is B's.
        var sourceTenantId = NewName("rgsrc");
        var legacyTenantId = NewName("rglegacy");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceB, legacyTenantId);
        try
        {
            await OwnershipStoreA.RemoveAsync(legacyTenantId, Ct);
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(legacyTenantId, legacyTenantId, archivePath);

            RestoreRefusalAssert.IsUniformAndLogged(result, fixture.Logs, legacyTenantId, legacyTenantId,
                "registered as a tenant database by another OctoMesh instance", SystemDatabaseB);
            Assert.Null(await InstanceA.TryFindTenantContextAsync(legacyTenantId));
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(legacyTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, legacyTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoSystemDatabaseOfAnotherInstance_IsRefused_EvenWithOverride()
    {
        // A system database is recognised by its tenant registry rows, so B needs one tenant.
        var sourceTenantId = NewName("rgsrc");
        var tenantOfB = NewName("rgtenantb");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceB, tenantOfB);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var refusedTenantId = NewName("rgsys");
            var refusedWithOverrideTenantId = NewName("rgsys");
            var refused = await InstanceA.RestoreTenantAsync(refusedTenantId, SystemDatabaseB, archivePath);
            var refusedWithOverride = await InstanceA.RestoreTenantAsync(refusedWithOverrideTenantId,
                SystemDatabaseB, archivePath, overrideForeignOwnership: true);

            RestoreRefusalAssert.IsUniformAndLogged(refused, fixture.Logs, refusedTenantId, SystemDatabaseB,
                "system database of another OctoMesh instance");
            RestoreRefusalAssert.IsUniformAndLogged(refusedWithOverride, fixture.Logs, refusedWithOverrideTenantId,
                SystemDatabaseB, "cannot be overridden");

            // B is untouched: its system tenant still resolves its tenant.
            Assert.True(await InstanceB.IsSystemTenantExistingAsync());
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(tenantOfB));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoOwnSystemDatabase_IsRefusedWithTheUniformConflict_AndLogsTheReason()
    {
        var sourceTenantId = NewName("rgsrc");
        var refusedTenantId = NewName("rgownsys");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var refused = await InstanceA.RestoreTenantAsync(refusedTenantId, SystemDatabaseA, archivePath);

            RestoreRefusalAssert.IsUniformAndLogged(refused, fixture.Logs, refusedTenantId, SystemDatabaseA,
                "system database of this instance");
            Assert.True(await InstanceA.IsSystemTenantExistingAsync());
            Assert.Null(await InstanceA.TryFindTenantContextAsync(refusedTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_RefusedForAnotherInstance_DoesNotDropTheExistingTenantOfThisInstance()
    {
        // dropExistingTenant is destructive itself, so the guard must refuse BEFORE it runs.
        var tenantId = NewName("rgkeep");
        var foreignTenantId = NewName("rgforeign");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, tenantId);
        await CreateTenantAsync(InstanceB, foreignTenantId);
        try
        {
            await BackupAsync(InstanceA, tenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(tenantId, foreignTenantId, archivePath,
                dropExistingTenant: true);

            Assert.False(result.Success);
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(tenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, foreignTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, tenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoNewDatabase_Succeeds_AndThisInstanceOwnsTheResult()
    {
        var sourceTenantId = NewName("rgsrc");
        var restoredTenantId = NewName("rgnew");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(restoredTenantId));
            var marker = await OwnershipStoreA.GetAsync(restoredTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseA, marker!.OwnerSystemDatabaseName);
            Assert.Equal(restoredTenantId, marker.TenantId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_OfADumpTakenOnAnotherInstance_IntoNewDatabase_AttachesAndRestampsTheOwner()
    {
        // The archive carries B's ownership marker. Without removing it, A's attach after the restore
        // would refuse the restored database as owned by B.
        var tenantOfB = NewName("rgdumpb");
        var restoredTenantId = NewName("rgimport");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceB, tenantOfB);
        try
        {
            await BackupAsync(InstanceB, tenantOfB, archivePath);

            var result = await InstanceA.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(restoredTenantId));
            var marker = await OwnershipStoreA.GetAsync(restoredTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseA, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
            await ThrowawayTenant.DropAsync(InstanceA, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_OverOwnExistingTenant_Succeeds()
    {
        var tenantId = NewName("rgown");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, tenantId);
        try
        {
            await BackupAsync(InstanceA, tenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(tenantId));
            var marker = await OwnershipStoreA.GetAsync(tenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseA, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, tenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoOwnDetachedDatabase_Succeeds()
    {
        // A detached database of this instance carries no marker (detach removes it) and no registry
        // row: restoring into it is a restore into an unowned database and works as before.
        var sourceTenantId = NewName("rgsrc");
        var detachedTenantId = NewName("rgdetached");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceA, detachedTenantId);
        try
        {
            using (var session = await InstanceA.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await InstanceA.DetachChildTenantAsync(session, detachedTenantId);
                await session.CommitTransactionAsync();
            }

            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(detachedTenantId, detachedTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(detachedTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, detachedTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoDatabaseMarkedByAnotherInstance_WithOverride_Succeeds_AndLogsTheOverride()
    {
        // A database the marker assigns to B while no system database registers it any more (B's
        // registry row is gone, e.g. B was removed): the override lets A take it over.
        var sourceTenantId = NewName("rgsrc");
        var orphanTenantId = NewName("rgoverride");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceB, orphanTenantId);
        try
        {
            await DetachQuietlyAsync(InstanceB, orphanTenantId);
            await OwnershipStoreA.StampAsync(orphanTenantId, orphanTenantId, SystemDatabaseB, Ct);
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var refused = await InstanceA.RestoreTenantAsync(orphanTenantId, orphanTenantId, archivePath);
            var result = await InstanceA.RestoreTenantAsync(orphanTenantId, orphanTenantId, archivePath,
                overrideForeignOwnership: true);

            RestoreRefusalAssert.IsUniformAndLogged(refused, fixture.Logs, orphanTenantId, orphanTenantId,
                SystemDatabaseB);
            Assert.True(result.Success, $"Restore with override should succeed. Error: {result.Error}");
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(orphanTenantId));
            var marker = await OwnershipStoreA.GetAsync(orphanTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseA, marker!.OwnerSystemDatabaseName);

            Assert.Contains(fixture.Logs.Entries, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("OWNERSHIP OVERRIDE", StringComparison.Ordinal) &&
                e.Message.Contains(orphanTenantId, StringComparison.Ordinal) &&
                e.Message.Contains(SystemDatabaseB, StringComparison.Ordinal));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, orphanTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_WithOverrideAndAttach_IntoDatabaseRegisteredByAnotherInstance_IsRefusedBeforeAnyWrite()
    {
        // The attach guard (AB#8796) refuses a database another instance registers. Restoring first would
        // replace B's data and then fail the attach — so the override refuses before any write.
        var sourceTenantId = NewName("rgsrc");
        var foreignTenantId = NewName("rgregd");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceB, foreignTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(foreignTenantId, foreignTenantId, archivePath,
                overrideForeignOwnership: true);

            RestoreRefusalAssert.IsUniformAndLogged(result, fixture.Logs, foreignTenantId, foreignTenantId,
                "register one database in two instances", SystemDatabaseB);
            Assert.Null(await InstanceA.TryFindTenantContextAsync(foreignTenantId));
            var marker = await OwnershipStoreA.GetAsync(foreignTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseB, marker!.OwnerSystemDatabaseName);
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(foreignTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, foreignTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_WithOverrideWithoutAttach_IntoDatabaseRegisteredByAnotherInstance_Succeeds_AndLogsTheOverride()
    {
        var sourceTenantId = NewName("rgsrc");
        var foreignTenantId = NewName("rgnoattach");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceB, foreignTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(foreignTenantId, foreignTenantId, archivePath,
                attachTenant: false, overrideForeignOwnership: true);

            Assert.True(result.Success, $"Restore with override should succeed. Error: {result.Error}");
            Assert.Null(await InstanceA.TryFindTenantContextAsync(foreignTenantId));
            Assert.Contains(fixture.Logs.Entries, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("OWNERSHIP OVERRIDE", StringComparison.Ordinal) &&
                e.Message.Contains(foreignTenantId, StringComparison.Ordinal) &&
                e.Message.Contains(SystemDatabaseB, StringComparison.Ordinal));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, foreignTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_WithOverride_IntoANewDatabase_DoesNotDropTheExistingTenantsOtherForeignMarkedDatabase()
    {
        // The override was checked against the TARGET only. The existing tenant lives in a different database
        // that carries an orphaned foreign marker: dropping it needs its own decision, the restore's override
        // does not cover it (AB#8672).
        const string goneInstance = "octosystemgone";
        var sourceTenantId = NewName("rgsrc");
        var existingTenantId = NewName("rgexisting");
        var newDatabase = NewName("rgnewdb");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        await CreateTenantAsync(InstanceA, existingTenantId);
        try
        {
            await OwnershipStoreA.StampAsync(existingTenantId, existingTenantId, goneInstance, Ct);
            await BackupAsync(InstanceA, sourceTenantId, archivePath);

            var result = await InstanceA.RestoreTenantAsync(existingTenantId, newDatabase, archivePath,
                overrideForeignOwnership: true);

            Assert.False(result.Success, "The restore must not drop the existing tenant's other database");
            Assert.Contains(TenantException.DatabaseNameNotAvailable(existingTenantId).Message, result.Error);
            Assert.True(await InstanceA.IsDatabaseExistingAsync(existingTenantId));
            var marker = await OwnershipStoreA.GetAsync(existingTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(goneInstance, marker!.OwnerSystemDatabaseName);
            var existing = await InstanceA.TryFindTenantContextAsync(existingTenantId);
            Assert.NotNull(existing);
            Assert.Equal(existingTenantId, existing!.DatabaseName.ToLowerInvariant());
            Assert.False(await InstanceA.IsDatabaseExistingAsync(newDatabase));
            Assert.DoesNotContain(fixture.Logs.Entries, e =>
                e.Message.StartsWith("OWNERSHIP OVERRIDE", StringComparison.Ordinal) &&
                e.Message.Contains(existingTenantId, StringComparison.Ordinal));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, existingTenantId, overrideForeignOwnership: true);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_HoldsAClaimOnItsTarget_AConcurrentAttachOfAnotherInstanceIsRefused_AndTheAttachTakesItOver()
    {
        // AB#8672: the restore claims the target before it drops or writes anything and holds the claim through
        // mongorestore. Instance B tries to attach the target right before and right after mongorestore ran - both
        // are refused with the uniform conflict, and A's attach at the end takes the claim over.
        var sourceTenantId = NewName("rgsrc");
        var restoredTenantId = NewName("rgclaim");
        var tenantIdB = NewName("rgclaimb");
        var archivePath = NewArchivePath();
        var attempts = new List<Exception?>();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);
            Func<Task> attachInB = async () => attempts.Add(await TryAttachAsync(InstanceB, restoredTenantId, tenantIdB));
            var service = CreateBackupServiceOfA(new InterceptingRepositoryOps(
                fixture.GetService<IRepositoryOpsService>(), beforeRestore: attachInB, afterRestore: attachInB));

            var result = await service.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.Equal(2, attempts.Count);
            Assert.All(attempts, attempt =>
            {
                var conflict = Assert.IsType<TenantException>(attempt);
                Assert.True(conflict.IsConflict);
                Assert.Equal(TenantException.DatabaseNameNotAvailable(restoredTenantId).Message, conflict.Message);
            });
            Assert.Null(await InstanceB.TryFindTenantContextAsync(tenantIdB));
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(restoredTenantId));
            var marker = await OwnershipStoreA.GetAsync(restoredTenantId, Ct);
            Assert.Equal(SystemDatabaseA, marker!.OwnerSystemDatabaseName);
            Assert.Equal(restoredTenantId, marker.TenantId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantIdB);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_ThatFailsAfterItsClaim_ReleasesTheClaim_AndLeavesNoDatabaseBehind()
    {
        // AB#8672: the claim materializes a new target. A restore that fails before anything was written drops that
        // database again, so the name is not blocked by an empty database; nothing of this instance remains on it.
        var sourceTenantId = NewName("rgsrc");
        var restoredTenantId = NewName("rgfail");
        var archivePath = NewArchivePath();
        var claimSeen = false;

        await CreateTenantAsync(InstanceA, sourceTenantId);
        try
        {
            await BackupAsync(InstanceA, sourceTenantId, archivePath);
            var service = CreateBackupServiceOfA(new InterceptingRepositoryOps(
                fixture.GetService<IRepositoryOpsService>(),
                beforeRestore: async () =>
                    claimSeen = (await OwnershipStoreA.GetAsync(restoredTenantId, Ct))?.OwnerSystemDatabaseName ==
                                SystemDatabaseA,
                failRestore: true));

            var result = await service.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.False(result.Success);
            Assert.True(claimSeen, "the target must be claimed before mongorestore runs");
            Assert.False(await InstanceA.IsDatabaseExistingAsync(restoredTenantId));
            Assert.Null(await InstanceA.TryFindTenantContextAsync(restoredTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    private TenantBackupService CreateBackupServiceOfA(IRepositoryOpsService repositoryOps) => new(
        InstanceA, repositoryOps, fixture.GetService<ILogger<TenantBackupService>>(), OwnershipStoreA,
        fixture.GetService<TenantDatabaseRegistrationLookup>(), fixture.GetService<SystemDatabaseMarkerStore>(),
        fixture.GetService<ITenantLifecycleStore>(), fixture.GetService<IOptions<OctoSystemConfiguration>>());

    private static async Task<Exception?> TryAttachAsync(ISystemContext instance, string databaseName, string tenantId)
    {
        try
        {
            using var session = await instance.GetAdminSessionAsync();
            session.StartTransaction();
            await instance.AttachChildTenantAsync(session, databaseName, tenantId);
            await session.CommitTransactionAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Runs code right before and after the real mongorestore of a restore, or fails it.</summary>
    private sealed class InterceptingRepositoryOps(IRepositoryOpsService inner, Func<Task>? beforeRestore = null,
        Func<Task>? afterRestore = null, bool failRestore = false) : IRepositoryOpsService
    {
        public Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath,
            CancellationToken? cancellationToken = null) =>
            inner.ExecuteMongoShellScriptAsync(databaseName, scriptPath, cancellationToken);

        public Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command,
            CancellationToken? cancellationToken = null) =>
            inner.ExecuteMongoShellCommandAsync(databaseName, command, cancellationToken);

        public Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options, TimeSpan? timeout = null,
            CancellationToken? cancellationToken = null) =>
            inner.ExecuteMongoDumpAsync(options, timeout, cancellationToken);

        public async Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options, TimeSpan? timeout = null,
            CancellationToken? cancellationToken = null)
        {
            if (beforeRestore != null)
            {
                await beforeRestore();
            }

            if (failRestore)
            {
                return CommandResult.Failure("mongorestore failed (test)");
            }

            var result = await inner.ExecuteMongoRestoreAsync(options, timeout, cancellationToken);
            if (afterRestore != null)
            {
                await afterRestore();
            }

            return result;
        }

        public Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
            inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);

        public Task<CommandResult> ExecuteCommandAsync(string fileName, IReadOnlyList<string> arguments,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
            inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
    }

    private static async Task CreateTenantAsync(ISystemContext instance, string tenantId)
    {
        using var session = await instance.GetAdminSessionAsync();
        session.StartTransaction();
        await instance.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task BackupAsync(ISystemContext instance, string tenantId, string archivePath)
    {
        var backup = await instance.BackupTenantAsync(tenantId, archivePath);
        Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");
    }

    private static async Task DetachQuietlyAsync(ISystemContext instance, string tenantId)
    {
        try
        {
            using var session = await instance.GetAdminSessionAsync();
            session.StartTransaction();
            await instance.DetachChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch (Exception)
        {
            // Cleanup only — must not mask the assertion failure.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup only.
        }
    }
}
