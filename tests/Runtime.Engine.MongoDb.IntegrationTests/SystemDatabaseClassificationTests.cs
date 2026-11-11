using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The one classification of a database as a system database (AB#8796) that attach, restore and delete share:
///     the system-database marker first, the parent tenant ids of the registry rows as the fallback. A detached
///     parent tenant keeps parent-local rows for its children, has no ownership marker and no registration anywhere;
///     it must not look like another instance's system database, or every re-attach and restore of a tenant with
///     children is refused. A real system database of another instance must stay refused on every path, with and
///     without its marker. Also the concurrent claim of the attach: two instances attaching the same unmarked
///     database cannot both win. Instance A is the fixture, instance B its <see cref="TwoInstanceFixture.Other" />.
/// </summary>
[Collection(TwoInstanceCollection.Name)]
public class SystemDatabaseClassificationTests(TwoInstanceFixture fixture)
{
    private ISystemContext InstanceA => fixture.GetSystemContext();

    private ISystemContext InstanceB => fixture.Other.GetSystemContext();

    private string SystemDatabaseA => fixture.SystemDatabaseName.ToLowerInvariant();

    private string SystemDatabaseB => fixture.Other.SystemDatabaseName.ToLowerInvariant();

    private TenantOwnershipStore OwnershipStore => fixture.GetService<TenantOwnershipStore>();

    private SystemDatabaseMarkerStore MarkerStore => fixture.GetService<SystemDatabaseMarkerStore>();

    private TenantDatabaseRegistrationLookup Lookup => fixture.GetService<TenantDatabaseRegistrationLookup>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static string NewArchivePath() => Path.Combine(Path.GetTempPath(), $"sysclass_{Guid.NewGuid():N}.gz");

    [Fact]
    public async Task DetachedParentWithChildren_IsNoSystemDatabase_AndCanBeAttachedAgain()
    {
        var parentId = NewName("scparent");
        var childId = NewName("scchild");

        await CreateAsync(InstanceA, parentId);
        try
        {
            await CreateAsync(await InstanceA.GetChildTenantContextAsync(parentId), childId);
            await InTransactionAsync(InstanceA, s => InstanceA.DetachChildTenantAsync(s, parentId));
            Assert.Null(await OwnershipStore.GetAsync(parentId, Ct));

            var registrations = await Lookup.GetRegistrationsAsync(Ct);
            Assert.False(registrations.IsSystemDatabase(parentId));
            Assert.Equal([SystemDatabaseA], registrations.GetRegisteringSystemDatabases(childId));

            await InTransactionAsync(InstanceA, s => InstanceA.AttachChildTenantAsync(s, parentId, parentId));

            var marker = await OwnershipStore.GetAsync(parentId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseA, marker!.OwnerSystemDatabaseName);
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(parentId));
        }
        finally
        {
            await DropChildQuietlyAsync(InstanceA, parentId, childId);
            await ThrowawayTenant.DropAsync(InstanceA, parentId);
        }
    }

    [Fact]
    public async Task BackupWithDetach_ThenRestoreWithAttach_OfParentWithChildren_Succeeds()
    {
        var parentId = NewName("scbkparent");
        var childId = NewName("scbkchild");
        var archivePath = NewArchivePath();

        await CreateAsync(InstanceA, parentId);
        try
        {
            await CreateAsync(await InstanceA.GetChildTenantContextAsync(parentId), childId);

            var backup = await InstanceA.BackupTenantAsync(parentId, archivePath, detachTenant: true);
            Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");
            Assert.Null(await InstanceA.TryFindTenantContextAsync(parentId));

            var restore = await InstanceA.RestoreTenantAsync(parentId, parentId, archivePath);

            Assert.True(restore.Success, $"Restore should succeed. Error: {restore.Error}");
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(parentId));
            Assert.Equal(SystemDatabaseA, (await OwnershipStore.GetAsync(parentId, Ct))!.OwnerSystemDatabaseName);
        }
        finally
        {
            await DropChildQuietlyAsync(InstanceA, parentId, childId);
            await ThrowawayTenant.DropAsync(InstanceA, parentId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task SystemDatabaseOfAnotherInstance_WithoutItsMarker_IsRecognisedByItsRegistry_OnEveryPath()
    {
        // B as it ran before the marker shipped: only its registry rows (they name their parent) say what it is.
        var tenantOfB = NewName("scnomarkb");
        var corruptIdA = NewName("scnomarkrow");
        var sourceTenantId = NewName("scnomarksrc");
        var archivePath = NewArchivePath();

        await MarkerStore.RemoveAsync(SystemDatabaseB, Ct);
        await CreateAsync(InstanceB, tenantOfB);
        await CreateAsync(InstanceA, sourceTenantId);
        try
        {
            Assert.Null(await MarkerStore.GetStampedAtUtcAsync(SystemDatabaseB, Ct));
            await AssertRefusedOnEveryPathAsync(corruptIdA, sourceTenantId, archivePath);
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(tenantOfB));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, corruptIdA);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
            // Back to the state of the fixture: B stamps its marker on start.
            await InstanceB.EnsureSystemCkModelAsync();
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task SystemDatabaseOfAnotherInstance_WithItsMarker_IsRefusedOnEveryPath_WithAndWithoutTenants()
    {
        var corruptIdA = NewName("scmarkrow");
        var sourceTenantId = NewName("scmarksrc");
        var tenantOfB = NewName("scmarkb");
        var archivePath = NewArchivePath();

        await CreateAsync(InstanceA, sourceTenantId);
        try
        {
            Assert.NotNull(await MarkerStore.GetStampedAtUtcAsync(SystemDatabaseB, Ct));

            // Without a tenant of this test (the marker alone) ...
            await AssertRefusedOnEveryPathAsync(corruptIdA, sourceTenantId, archivePath);
            await UnregisterDirectlyQuietlyAsync(InstanceA, corruptIdA);

            // ... and with one.
            await CreateAsync(InstanceB, tenantOfB);
            await AssertRefusedOnEveryPathAsync(corruptIdA, sourceTenantId, archivePath);
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(tenantOfB));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, corruptIdA);
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task ConcurrentAttach_WhenAnotherInstanceClaimsTheDatabaseFirst_IsRefused_AndKeepsTheWinnersMarker()
    {
        var tenantId = NewName("scrace");
        await CreateAsync(InstanceB, tenantId);
        await InTransactionAsync(InstanceB, s => InstanceB.DetachChildTenantAsync(s, tenantId));
        Assert.Null(await OwnershipStore.GetAsync(tenantId, Ct));

        // B claims the free database after A's namespace check and before A's own claim: the window of two
        // instances attaching the same unmarked database at the same time.
        fixture.Notifications.OnPreTenantCreate = async id =>
        {
            if (id == tenantId)
            {
                await OwnershipStore.StampIfAbsentAndGetAsync(tenantId, tenantId, SystemDatabaseB, Ct);
            }
        };
        try
        {
            var exception = await Assert.ThrowsAsync<TenantException>(() =>
                InTransactionAsync(InstanceA, s => InstanceA.AttachChildTenantAsync(s, tenantId, tenantId)));

            Assert.True(exception.IsConflict);
            Assert.Equal(TenantException.DatabaseNameNotAvailable(tenantId).Message, exception.Message);
            Assert.Contains(fixture.Logs.Entries, e =>
                e.Message.Contains($"Rejected database name '{tenantId}'", StringComparison.Ordinal) &&
                e.Message.Contains("claimed the database concurrently", StringComparison.Ordinal));

            // The winner keeps its claim, the loser registered nothing.
            var marker = await OwnershipStore.GetAsync(tenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(SystemDatabaseB, marker!.OwnerSystemDatabaseName);
            Assert.Null(await InstanceA.TryFindTenantContextAsync(tenantId));
        }
        finally
        {
            fixture.Notifications.OnPreTenantCreate = null;

            // Only after a failed assertion A holds the tenant (with B's marker): drop it there.
            await ThrowawayTenant.DropAsync(InstanceA, tenantId, overrideForeignOwnership: true);
            if (await InstanceB.IsDatabaseExistingAsync(tenantId))
            {
                await OwnershipStore.RemoveAsync(tenantId, Ct);
                await InTransactionAsync(InstanceB, s => InstanceB.AttachChildTenantAsync(s, tenantId, tenantId));
            }

            await ThrowawayTenant.DropAsync(InstanceB, tenantId);
        }
    }

    /// <summary>B's system database is refused as attach target, as restore target and as the database of a delete.</summary>
    private async Task AssertRefusedOnEveryPathAsync(string corruptIdA, string sourceTenantId, string archivePath)
    {
        var attachId = NewName("scattach");
        var attach = await Assert.ThrowsAsync<TenantException>(() =>
            InTransactionAsync(InstanceA, s => InstanceA.AttachChildTenantAsync(s, SystemDatabaseB, attachId)));
        Assert.True(attach.IsConflict);
        Assert.Null(await InstanceA.TryFindTenantContextAsync(attachId));
        Assert.Null(await OwnershipStore.GetAsync(SystemDatabaseB, Ct));

        DeleteQuietly(archivePath);
        var backup = await InstanceA.BackupTenantAsync(sourceTenantId, archivePath);
        Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");
        var restore = await InstanceA.RestoreTenantAsync(NewName("screstore"), SystemDatabaseB, archivePath,
            overrideForeignOwnership: true);
        Assert.False(restore.Success);
        Assert.Contains("system database of another OctoMesh instance", restore.Error);

        await RegisterDirectlyAsync(InstanceA, corruptIdA, SystemDatabaseB);
        var delete = await Assert.ThrowsAsync<TenantException>(() =>
            InTransactionAsync(InstanceA,
                s => InstanceA.DropChildTenantAsync(s, corruptIdA, overrideForeignOwnership: true)));
        Assert.True(delete.IsConflict);
        Assert.True(await InstanceB.IsSystemTenantExistingAsync());
    }

    private static async Task InTransactionAsync(ITenantContext context, Func<IOctoAdminSession, Task> action)
    {
        using var session = await context.GetAdminSessionAsync();
        session.StartTransaction();
        await action(session);
        await session.CommitTransactionAsync();
    }

    private static Task CreateAsync(ITenantContext context, string tenantId) =>
        InTransactionAsync(context, s => context.CreateChildTenantAsync(s, tenantId, tenantId));

    private static Task RegisterDirectlyAsync(ISystemContext instance, string tenantId, string databaseName) =>
        InTransactionAsync(instance, s => instance.GetTenantRepositoryAsAdmin().InsertOneRtEntityAsync(s,
            new RtTenant { TenantId = tenantId, ParentTenantId = instance.TenantId, DatabaseName = databaseName }));

    private static async Task UnregisterDirectlyQuietlyAsync(ISystemContext instance, string tenantId)
    {
        try
        {
            await InTransactionAsync(instance, s => instance.GetTenantRepositoryAsAdmin()
                .DeleteManyRtEntitiesAsync<RtTenant>(s,
                    FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId), tenantId),
                    DeleteOptions.Erase));
        }
        catch (Exception)
        {
            // Cleanup only.
        }
    }

    private static async Task DropChildQuietlyAsync(ISystemContext instance, string parentId, string childId)
    {
        try
        {
            var parent = await instance.GetChildTenantContextAsync(parentId);
            await InTransactionAsync(parent, s => parent.DropChildTenantAsync(s, childId));
        }
        catch (Exception)
        {
            // Cleanup only.
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
