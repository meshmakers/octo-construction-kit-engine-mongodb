using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Services set a tenant up when they receive the tenant-created notification, so it must not be published before
///     the create or attach is committed: inside the caller's transaction it is published after the commit (a few
///     attempts, then a warning), never on a rollback; without a transaction at once; a restore publishes it itself
///     after its commit and flags a failed publish on its (successful) result.
/// </summary>
[Collection(TenantNotificationCollection.Name)]
public class TenantCreatedNotificationOrderTests(TenantNotificationFixture fixture)
{
    private static string NewTenantId(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task CreateInATransaction_IsPublishedAfterTheCommit_WithTheTenantRegistered()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("notecr");
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                Assert.Empty(fixture.Notifications.PosCreatedFor(tenantId));
                await session.CommitTransactionAsync();
            }

            var published = Assert.Single(fixture.Notifications.PosCreatedFor(tenantId));
            Assert.True(published.Registered, "the services must find the tenant registered when they set it up");
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task AttachInATransaction_IsPublishedAfterTheCommit_WithTheTenantRegistered()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("noteat");
        try
        {
            await CreateAndDetachAsync(tenantId);
            var before = fixture.Notifications.PosCreatedFor(tenantId).Count;

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                Assert.Equal(before, fixture.Notifications.PosCreatedFor(tenantId).Count);
                await session.CommitTransactionAsync();
            }

            var published = fixture.Notifications.PosCreatedFor(tenantId).Skip(before).ToList();
            Assert.True(Assert.Single(published).Registered);
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task AttachInATransactionThatIsRolledBack_PublishesNothing()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("noterb");
        try
        {
            await CreateAndDetachAsync(tenantId);
            var before = fixture.Notifications.PosCreatedFor(tenantId).Count;

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.AbortTransactionAsync();
            }

            Assert.Equal(before, fixture.Notifications.PosCreatedFor(tenantId).Count);
            Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId));

            // Attach again (committed) so the cleanup can drop it.
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task ClearInATransaction_PublishesDeleteCreateThenUpdate_AfterTheCommit()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("notecl");
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var before = fixture.Notifications.PublishedFor(tenantId).Count;
            var createsBefore = fixture.Notifications.PosCreatedFor(tenantId).Count;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.ClearChildTenantAsync(session, tenantId);
                Assert.Equal(before, fixture.Notifications.PublishedFor(tenantId).Count);
                await session.CommitTransactionAsync();
            }

            Assert.Equal(["Delete", "Create", "Update"], fixture.Notifications.PublishedFor(tenantId).Skip(before));
            Assert.True(Assert.Single(fixture.Notifications.PosCreatedFor(tenantId).Skip(createsBefore)).Registered);
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task CreateWithoutATransaction_IsPublishedAtOnce()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("notent");
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                var published = Assert.Single(fixture.Notifications.PosCreatedFor(tenantId));
                Assert.True(published.Registered);
            }
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
        var tenantId = NewTenantId("notert");
        try
        {
            var attemptsBefore = fixture.Notifications.PosCreateAttempts;
            fixture.Notifications.FailNext(2);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.Equal(attemptsBefore + 3, fixture.Notifications.PosCreateAttempts);
            Assert.True(Assert.Single(fixture.Notifications.PosCreatedFor(tenantId)).Registered);
            Assert.DoesNotContain(fixture.Logs.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains(tenantId, StringComparison.OrdinalIgnoreCase) &&
                     e.Message.Contains("ClearCache", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Notifications.FailNext(0);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task PublishAfterTheCommit_FailingEveryAttempt_KeepsTheTenant_AndWarnsWithTheRecovery()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("notewn");
        try
        {
            var attemptsBefore = fixture.Notifications.PosCreateAttempts;
            fixture.Notifications.FailNext(3);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.Equal(attemptsBefore + 3, fixture.Notifications.PosCreateAttempts);
            Assert.Empty(fixture.Notifications.PosCreatedFor(tenantId));
            Assert.True(await systemContext.IsTenantRegisteredAsync(tenantId), "the commit stands");
            Assert.Contains(fixture.Logs.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains(tenantId, StringComparison.OrdinalIgnoreCase) &&
                     e.Message.Contains("ClearCache", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Notifications.FailNext(0);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task Restore_PublishesAfterItsCommit_WithTheTenantRegistered()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("noters");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await CreateBackupAndDropAsync(tenantId, archivePath);
            var before = fixture.Notifications.PosCreatedFor(tenantId).Count;

            var result = await systemContext.RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true, attachTenant: true);

            Assert.True(result.Success, result.Error);
            var published = fixture.Notifications.PosCreatedFor(tenantId).Skip(before).ToList();
            Assert.True(Assert.Single(published).Registered);
        }
        finally
        {
            DeleteFile(archivePath);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task Restore_WhosePublishFails_Succeeds_WithTheFlagAndTheRecovery_AndKeepsTheTenantAttached()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("noterf");
        var archivePath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.tar.gz");
        try
        {
            await CreateBackupAndDropAsync(tenantId, archivePath);
            var attemptsBefore = fixture.Notifications.PosCreateAttempts;
            fixture.Notifications.FailNext(1);

            var result = await systemContext.RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true, attachTenant: true);

            Assert.True(result.Success, "the data is restored and attached; only the setup is missing");
            Assert.True(result.TenantCreatedNotificationFailed);
            Assert.Contains("ClearCache", result.Output, StringComparison.Ordinal);
            Assert.Contains(tenantId, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(attemptsBefore + 1, fixture.Notifications.PosCreateAttempts);
            Assert.True(await systemContext.IsTenantRegisteredAsync(tenantId), "the attach is committed");
        }
        finally
        {
            fixture.Notifications.FailNext(0);
            DeleteFile(archivePath);
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task Clone_WhosePublishFails_Succeeds_WithTheFlag()
    {
        var systemContext = fixture.GetSystemContext();
        var sourceTenantId = NewTenantId("notcls");
        var tempTenantId = NewTenantId("notclt");
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, sourceTenantId, sourceTenantId);
                await session.CommitTransactionAsync();
            }

            fixture.Notifications.FailNext(1);
            var cloned = await systemContext.CloneTenantToTempAsync(sourceTenantId, tempTenantId, tempTenantId);

            Assert.True(cloned.Success, cloned.Error);
            Assert.True(cloned.TenantCreatedNotificationFailed);
            Assert.Contains("ClearCache", cloned.Output, StringComparison.Ordinal);
            Assert.True(await systemContext.IsTenantRegisteredAsync(tempTenantId));
        }
        finally
        {
            fixture.Notifications.FailNext(0);
            await DropAsync(tempTenantId);
            await DropAsync(sourceTenantId);
        }
    }

    [Fact]
    public async Task AttachAndDetachInOneTransaction_PublishesNoTenantCreated()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("noteds");
        try
        {
            await CreateAndDetachAsync(tenantId);
            var before = fixture.Notifications.PosCreatedFor(tenantId).Count;

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId));
            Assert.Equal(before, fixture.Notifications.PosCreatedFor(tenantId).Count);

            // Attach again (committed) so the cleanup can drop it.
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task AttachDetachAttachInOneTransaction_PublishesOnce_ForTheLastAttach()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("notead");
        try
        {
            await CreateAndDetachAsync(tenantId);
            var before = fixture.Notifications.PosCreatedFor(tenantId).Count;

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await systemContext.DetachChildTenantAsync(session, tenantId);
                await systemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var published = fixture.Notifications.PosCreatedFor(tenantId).Skip(before).ToList();
            Assert.True(Assert.Single(published).Registered);
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    [Fact]
    public async Task ScopeOfAnotherSession_DoesNotHoldBackACreate()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId("notsco");
        try
        {
            using var scopeSession = await systemContext.GetAdminSessionAsync();
            using var scope = TenantNotificationScope.Begin(scopeSession);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.True(Assert.Single(fixture.Notifications.PosCreatedFor(tenantId)).Registered,
                "a create in another session publishes after its own commit, not into the scope");
        }
        finally
        {
            await DropAsync(tenantId);
        }
    }

    private async Task CreateAndDetachAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.DetachChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
    }

    private async Task CreateBackupAndDropAsync(string tenantId, string archivePath)
    {
        var systemContext = fixture.GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        var backup = await systemContext.BackupTenantAsync(tenantId, archivePath);
        Assert.True(backup.Success, backup.Error);

        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
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
