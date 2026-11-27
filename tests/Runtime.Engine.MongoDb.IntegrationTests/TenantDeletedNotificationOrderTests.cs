using FakeItEasy;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Services tear a tenant down when they receive the tenant-deleted notification, so it must not be published before
///     the drop or detach is committed: inside the caller's transaction it is published after the commit (a few attempts,
///     then a warning), never on a rollback; without a transaction (the two-phase delete) at once; the backup's detach and
///     the restore's drop publish it themselves after their commit and name a failed publish in the result's output.
/// </summary>
[Collection(TenantNotificationCollection.Name)]
public class TenantDeletedNotificationOrderTests(TenantNotificationFixture fixture)
{
    private static string NewTenantId(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task DetachInATransaction_IsPublishedAfterTheCommit_WithTheTenantUnregistered()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("deldet");
        try
        {
            await CreateAsync(tenantId);

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
                Assert.Empty(fixture.Notifications.PosDeletedFor(tenantId));
                await session.CommitTransactionAsync();
            }

            var published = Assert.Single(fixture.Notifications.PosDeletedFor(tenantId));
            Assert.False(published.Registered, "the services must find the tenant gone when they tear it down");
        }
        finally
        {
            await ReattachAndDropAsync(tenantId);
        }
    }

    [Fact]
    public async Task DropInATransaction_IsPublishedAfterTheCommit_WithTheTenantUnregistered()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("deldrp");
        try
        {
            await CreateAsync(tenantId);

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId);
                Assert.Empty(fixture.Notifications.PosDeletedFor(tenantId));
                await session.CommitTransactionAsync();
            }

            var published = Assert.Single(fixture.Notifications.PosDeletedFor(tenantId));
            Assert.False(published.Registered);
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task DetachInATransactionThatIsRolledBack_PublishesNothing_AndTheTenantStays()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delrbk");
        try
        {
            await CreateAsync(tenantId);

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await session.AbortTransactionAsync();
            }

            Assert.Empty(fixture.Notifications.PosDeletedFor(tenantId));
            Assert.True(await systemContext.IsTenantRegisteredAsync(tenantId), "the rollback keeps the tenant");
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task DetachInATransactionDisposedWithoutCommit_PublishesNothing()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("deldsp");
        try
        {
            await CreateAsync(tenantId);

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
            }

            Assert.Empty(fixture.Notifications.PosDeletedFor(tenantId));
            Assert.True(await systemContext.IsTenantRegisteredAsync(tenantId));
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task TwoPhaseDelete_PublishesWhenTheDatabaseIsDropped_AfterTheCommittedMetadataDelete()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("del2ph");
        try
        {
            await CreateAsync(tenantId);

            Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantDeletionHandle handle;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                handle = await systemContext.DeleteChildTenantMetadataAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.Empty(fixture.Notifications.PosDeletedFor(tenantId));
            await systemContext.DropTenantDatabaseAsync(handle, tenantId);

            Assert.False(Assert.Single(fixture.Notifications.PosDeletedFor(tenantId)).Registered);
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task PublishAfterTheCommit_IsRetried_AndSucceeds()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delrty");
        try
        {
            await CreateAsync(tenantId);
            var attemptsBefore = fixture.Notifications.PosDeleteAttempts;
            fixture.Notifications.FailNextDelete(2);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.Equal(attemptsBefore + 3, fixture.Notifications.PosDeleteAttempts);
            Assert.False(Assert.Single(fixture.Notifications.PosDeletedFor(tenantId)).Registered);
            Assert.DoesNotContain(fixture.Logs.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains(tenantId, StringComparison.OrdinalIgnoreCase) &&
                     e.Message.Contains("tenant-deleted", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Notifications.FailNextDelete(0);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task PublishAfterTheCommit_FailingEveryAttempt_KeepsTheRemoval_AndWarnsWithTheRecovery()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delwrn");
        try
        {
            await CreateAsync(tenantId);
            var attemptsBefore = fixture.Notifications.PosDeleteAttempts;
            fixture.Notifications.FailNextDelete(3);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.Equal(attemptsBefore + 3, fixture.Notifications.PosDeleteAttempts);
            Assert.Empty(fixture.Notifications.PosDeletedFor(tenantId));
            Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId), "the commit stands");
            Assert.Contains(fixture.Logs.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains(tenantId, StringComparison.OrdinalIgnoreCase) &&
                     e.Message.Contains("tenant-deleted", StringComparison.Ordinal) &&
                     e.Message.Contains("ClearCache", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Notifications.FailNextDelete(0);
            await ReattachAndDropAsync(tenantId);
        }
    }

    [Fact]
    public async Task BackupWithDetach_PublishesAfterItsCommit_WithTheTenantUnregistered()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delbkp");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await CreateAsync(tenantId);

            var result = await systemContext.BackupTenantAsync(tenantId, archivePath, detachTenant: true);

            Assert.True(result.Success, result.Error);
            Assert.False(Assert.Single(fixture.Notifications.PosDeletedFor(tenantId)).Registered);
            Assert.DoesNotContain("tenant-deleted", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFile(archivePath);
            await ReattachAndDropAsync(tenantId);
        }
    }

    [Fact]
    public async Task BackupWithDetach_WhosePublishFails_Succeeds_WithTheRecoveryInTheOutput()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delbkf");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await CreateAsync(tenantId);
            var attemptsBefore = fixture.Notifications.PosDeleteAttempts;
            fixture.Notifications.FailNextDelete(1);

            var result = await systemContext.BackupTenantAsync(tenantId, archivePath, detachTenant: true);

            Assert.True(result.Success, "the detach is committed and the dump ran; only the event is missing");
            Assert.Equal(attemptsBefore + 1, fixture.Notifications.PosDeleteAttempts);
            Assert.Contains("tenant-deleted", result.Output, StringComparison.Ordinal);
            Assert.Contains("ClearCache", result.Output, StringComparison.Ordinal);
            Assert.Contains(tenantId, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId), "the detach is committed");
        }
        finally
        {
            fixture.Notifications.FailNextDelete(0);
            DeleteFile(archivePath);
            await ReattachAndDropAsync(tenantId);
        }
    }

    [Fact]
    public async Task RestoreOverAnExistingTenant_PublishesDeleteAfterTheDropsCommit_ThenCreate()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delrso");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await CreateAsync(tenantId);
            var backup = await systemContext.BackupTenantAsync(tenantId, archivePath);
            Assert.True(backup.Success, backup.Error);
            var before = fixture.Notifications.PublishedFor(tenantId).Count;
            var deletesBefore = fixture.Notifications.PosDeletedFor(tenantId).Count;

            var result = await systemContext.RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true, attachTenant: true);

            Assert.True(result.Success, result.Error);
            Assert.Equal(["Delete", "Create"], fixture.Notifications.PublishedFor(tenantId).Skip(before));
            Assert.False(Assert.Single(fixture.Notifications.PosDeletedFor(tenantId).Skip(deletesBefore)).Registered);
        }
        finally
        {
            DeleteFile(archivePath);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task RestoreOverAnExistingTenant_WhoseDeletePublishFails_Succeeds_WithTheRecoveryInTheOutput()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delrsf");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await CreateAsync(tenantId);
            var backup = await systemContext.BackupTenantAsync(tenantId, archivePath);
            Assert.True(backup.Success, backup.Error);
            var createsBefore = fixture.Notifications.PosCreatedFor(tenantId).Count;
            fixture.Notifications.FailNextDelete(1);

            var result = await systemContext.RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true, attachTenant: true);

            Assert.True(result.Success, "the data is restored and attached; only the delete event is missing");
            Assert.False(result.TenantCreatedNotificationFailed);
            Assert.Contains("tenant-deleted", result.Output, StringComparison.Ordinal);
            Assert.Contains("ClearCache", result.Output, StringComparison.Ordinal);
            Assert.True(Assert.Single(fixture.Notifications.PosCreatedFor(tenantId).Skip(createsBefore)).Registered,
                "the attach still publishes its tenant-created notification");
        }
        finally
        {
            fixture.Notifications.FailNextDelete(0);
            DeleteFile(archivePath);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task BackupWithDetach_WhosePublishFails_AndWhoseDumpThrows_NamesTheRecoveryInTheFailure()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delbkt");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        var ops = RepositoryOpsThatThrow();
        A.CallTo(() => ops.ExecuteMongoDumpAsync(A<MongoDumpOptions>._, A<TimeSpan?>._, A<CancellationToken?>._))
            .ThrowsAsync(new InvalidOperationException("mongodump crashed (test)"));
        try
        {
            await CreateAsync(tenantId);
            fixture.Notifications.FailNextDelete(1);

            var result = await BackupServiceWith(ops).BackupTenantAsync(tenantId, archivePath, detachTenant: true);

            Assert.False(result.Success);
            Assert.Contains("mongodump crashed (test)", result.Error, StringComparison.Ordinal);
            Assert.Contains("tenant-deleted", result.Output, StringComparison.Ordinal);
            Assert.Contains("run ClearCache for the tenant", result.Output, StringComparison.Ordinal);
            Assert.Contains(tenantId, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId), "the detach is committed");
        }
        finally
        {
            fixture.Notifications.FailNextDelete(0);
            DeleteFile(archivePath);
            await ReattachAndDropAsync(tenantId);
        }
    }

    [Fact]
    public async Task BackupWithDetach_WhoseDumpThrows_AfterAPublishedNotification_HasNoRecoveryLine()
    {
        var tenantId = NewTenantId("delbkn");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        var ops = RepositoryOpsThatThrow();
        A.CallTo(() => ops.ExecuteMongoDumpAsync(A<MongoDumpOptions>._, A<TimeSpan?>._, A<CancellationToken?>._))
            .ThrowsAsync(new InvalidOperationException("mongodump crashed (test)"));
        try
        {
            await CreateAsync(tenantId);

            var result = await BackupServiceWith(ops).BackupTenantAsync(tenantId, archivePath, detachTenant: true);

            Assert.False(result.Success);
            Assert.DoesNotContain("ClearCache", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFile(archivePath);
            await ReattachAndDropAsync(tenantId);
        }
    }

    [Fact]
    public async Task RestoreOverAnExistingTenant_WhoseDeletePublishFails_AndWhoseRestoreThrows_NamesTheRecoveryInTheFailure()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delrst");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        var ops = RepositoryOpsThatThrow();
        A.CallTo(() => ops.ExecuteMongoRestoreAsync(A<MongoRestoreOptions>.That.Matches(o => IsTargetRestore(o)),
                A<TimeSpan?>._, A<CancellationToken?>._))
            .ThrowsAsync(new InvalidOperationException("mongorestore crashed (test)"));
        try
        {
            await CreateAsync(tenantId);
            var backup = await systemContext.BackupTenantAsync(tenantId, archivePath);
            Assert.True(backup.Success, backup.Error);
            fixture.Notifications.FailNextDelete(1);

            var result = await BackupServiceWith(ops).RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true, attachTenant: true);

            Assert.False(result.Success);
            Assert.Contains("Exception during restore: mongorestore crashed (test)", result.Error, StringComparison.Ordinal);
            Assert.Contains("tenant-deleted", result.Output, StringComparison.Ordinal);
            Assert.Contains("run ClearCache for the tenant", result.Output, StringComparison.Ordinal);
            Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId), "the drop is committed");
        }
        finally
        {
            fixture.Notifications.FailNextDelete(0);
            DeleteFile(archivePath);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task RestoreThatThrows_WithoutAFailedNotification_FailsAsBefore_WithoutARecoveryLine()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delrsn");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        var ops = RepositoryOpsThatThrow();
        A.CallTo(() => ops.ExecuteMongoRestoreAsync(A<MongoRestoreOptions>.That.Matches(o => IsTargetRestore(o)),
                A<TimeSpan?>._, A<CancellationToken?>._))
            .ThrowsAsync(new InvalidOperationException("mongorestore crashed (test)"));
        try
        {
            await CreateAsync(tenantId);
            var backup = await systemContext.BackupTenantAsync(tenantId, archivePath);
            Assert.True(backup.Success, backup.Error);

            var result = await BackupServiceWith(ops).RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true, attachTenant: true);

            Assert.False(result.Success);
            Assert.Equal("Exception during restore: mongorestore crashed (test)", result.Error);
            Assert.DoesNotContain("ClearCache", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFile(archivePath);
            await DropAsync(tenantId);
        }
    }

    /// <summary>
    ///     The restore of the target, not the scratch restore of the CK collections the restore check of service-managed
    ///     models runs first.
    /// </summary>
    private static bool IsTargetRestore(MongoRestoreOptions options) =>
        !options.Database.Contains("restorecheck_", StringComparison.Ordinal);

    /// <summary>The fixture's mongo tools; a test makes one call of them throw.</summary>
    private IRepositoryOpsService RepositoryOpsThatThrow() =>
        A.Fake<IRepositoryOpsService>(o => o.Wrapping(fixture.GetService<IRepositoryOpsService>()));

    /// <summary>The backup service of the fixture's system tenant, with <paramref name="repositoryOps" />.</summary>
    private TenantBackupService BackupServiceWith(IRepositoryOpsService repositoryOps)
    {
        var provider = fixture.Provider!;
        return new TenantBackupService(fixture.GetSystemContext(), repositoryOps,
            NullLogger<TenantBackupService>.Instance, provider.GetRequiredService<TenantOwnershipStore>(),
            provider.GetRequiredService<TenantDatabaseRegistrationLookup>(),
            provider.GetRequiredService<SystemDatabaseMarkerStore>(),
            provider.GetRequiredService<Services.RestoreSystemModelCheck.RestoreSystemModelGuard>(),
            provider.GetService<ITenantLifecycleStore>(),
            provider.GetService<IOptions<OctoSystemConfiguration>>());
    }

    [Fact]
    public async Task ScopeOfAnotherSession_DoesNotHoldBackADetach()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("delsco");
        try
        {
            await CreateAsync(tenantId);
            using var scopeSession = await systemContext.GetAdminSessionAsync();
            using var scope = TenantNotificationScope.Begin(scopeSession);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.False(Assert.Single(fixture.Notifications.PosDeletedFor(tenantId)).Registered,
                "a detach in another session publishes after its own commit, not into the scope");
        }
        finally
        {
            await ReattachAndDropAsync(tenantId);
        }
    }

    private async Task CreateAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    /// <summary>Attaches a detached tenant again (committed) and drops it.</summary>
    private async Task ReattachAndDropAsync(string tenantId)
    {
        try
        {
            var systemContext = fixture.GetSystemContext();
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                if (!await systemContext.IsChildTenantExistingAsync(session, tenantId))
                {
                    await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                }

                await session.CommitTransactionAsync();
            }
        }
        catch
        {
            // Cleanup only.
        }

        await DropAsync(tenantId);
    }

    private async Task DropAsync(string tenantId)
    {
        try
        {
            var systemContext = fixture.GetSystemContext();
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            if (await systemContext.IsChildTenantExistingAsync(session, tenantId))
            {
                await systemContext.DropChildTenantAsync(session, tenantId);
            }

            await session.CommitTransactionAsync();
        }
        catch
        {
            // Cleanup only.
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleanup only.
        }
    }
}
