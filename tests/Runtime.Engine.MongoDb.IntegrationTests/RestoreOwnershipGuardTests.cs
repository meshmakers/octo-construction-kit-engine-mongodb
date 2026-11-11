using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Microsoft.Extensions.Logging;

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
