using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#6759: a tenant deleted from the system tenant left its row in the parent's own registry. The parent kept
///     listing it, a second delete from the parent ran the full delete on the stale row — events, registry delete and
///     the database drop for whatever tenant now used the id and database name — and the reconcile would have
///     re-registered it. These tests pin the fix: delete cleans the parent row, stale rows are never deleted through,
///     only removed on their own, create purges them, and the reconcile reports instead of resurrecting them.
/// </summary>
[Collection(TenantNamespaceCollection.Name)]
public class TenantStaleChildEntryTests(TenantNamespaceFixture fixture)
{
    private ISystemContext SystemContext => fixture.GetSystemContext();

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static async Task CreateTenantAsync(ITenantContext parent, string databaseName, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task DropTenantAsync(ITenantContext parent, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.DropChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task DropTenantQuietlyAsync(ITenantContext parent, string tenantId)
    {
        try
        {
            await DropTenantAsync(parent, tenantId);
        }
        catch (Exception)
        {
            // Cleanup only.
        }
    }

    private static async Task DetachTenantAsync(ITenantContext parent, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.DetachChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task<bool> RemoveStaleAsync(ITenantContext parent, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        var removed = await parent.RemoveStaleChildTenantEntryAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return removed;
    }

    private static async Task<OctoTenant[]> GetChildrenAsync(ITenantContext parent)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        var children = (await parent.GetChildTenantsAsync(session)).Items.ToArray();
        await session.CommitTransactionAsync();
        return children;
    }

    /// <summary>Writes a parent-local row directly — the state a pre-fix delete from the system tenant left behind.</summary>
    private static async Task InsertLocalRowAsync(ITenantContext parent, string tenantId, string databaseName)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.GetTenantRepositoryAsAdmin().InsertOneRtEntityAsync(session,
            new RtTenant { TenantId = tenantId, DatabaseName = databaseName });
        await session.CommitTransactionAsync();
    }

    private static async Task DeleteLocalRowsQuietlyAsync(ITenantContext parent, string tenantId)
    {
        try
        {
            using var session = await parent.GetAdminSessionAsync();
            session.StartTransaction();
            await parent.GetTenantRepositoryAsAdmin().DeleteManyRtEntitiesAsync<RtTenant>(session,
                FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId), tenantId), DeleteOptions.Erase);
            await session.CommitTransactionAsync();
        }
        catch (Exception)
        {
            // Cleanup only.
        }
    }

    private async Task RemoveSystemRegistryRowAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.GetTenantRepositoryAsAdmin().DeleteOneRtEntityAsync<RtTenant>(session,
            FieldFilterCriteria.Create().FieldEquals(nameof(RtTenant.TenantId), tenantId), DeleteOptions.Erase);
        await session.CommitTransactionAsync();
    }

    private async Task<OctoTenant?> GetRegistryRowAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        return (await SystemContext.GetAllTenantsAsync(session)).Items.SingleOrDefault(t => t.TenantId == tenantId);
    }

    private async Task<TenantRegistryReconcileResult> ReconcileAsync(bool apply)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var result = await SystemContext.ReconcileTenantRegistryAsync(session, apply);
        await session.CommitTransactionAsync();
        return result;
    }

    [Fact]
    public async Task Delete_FromTheSystemTenant_RemovesTheParentRow_AndIsIdempotent()
    {
        var parentId = NewName("sdparent");
        var childId = NewName("sdchild");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await CreateTenantAsync(parent, childId, childId);
            Assert.Contains(await GetChildrenAsync(parent), t => t.TenantId == childId);

            await DropTenantAsync(SystemContext, childId);

            Assert.DoesNotContain(await GetChildrenAsync(parent), t => t.TenantId == childId);
            Assert.Null(await GetRegistryRowAsync(childId));
            Assert.False(await SystemContext.IsDatabaseExistingAsync(childId));

            // Again, from either side: nothing left, nothing to delete.
            var fromSystem = await Assert.ThrowsAsync<TenantException>(() => DropTenantAsync(SystemContext, childId));
            Assert.False(fromSystem.IsConflict);
            await Assert.ThrowsAsync<TenantException>(() => DropTenantAsync(parent, childId));
            Assert.False(await RemoveStaleAsync(parent, childId));
        }
        finally
        {
            await DropTenantQuietlyAsync(parent, childId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task Detach_FromTheSystemTenant_RemovesTheParentRow()
    {
        var parentId = NewName("stparent");
        var childId = NewName("stchild");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await CreateTenantAsync(parent, childId, childId);

            await DetachTenantAsync(SystemContext, childId);

            Assert.DoesNotContain(await GetChildrenAsync(parent), t => t.TenantId == childId);
            Assert.Null(await GetRegistryRowAsync(childId));
            Assert.True(await SystemContext.IsDatabaseExistingAsync(childId), "detach keeps the database");
        }
        finally
        {
            try
            {
                // Re-attach the detached database so the drop below removes it.
                using var session = await SystemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, childId, childId);
                await session.CommitTransactionAsync();
            }
            catch (Exception)
            {
                // Still attached (the test failed before the detach) — the drops below handle it.
            }

            await DropTenantQuietlyAsync(SystemContext, childId);
            await DropTenantQuietlyAsync(parent, childId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task Delete_FromTheParent_StillRemovesBothRows()
    {
        var parentId = NewName("pdparent");
        var childId = NewName("pdchild");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await CreateTenantAsync(parent, childId, childId);

            await DropTenantAsync(parent, childId);

            Assert.DoesNotContain(await GetChildrenAsync(parent), t => t.TenantId == childId);
            Assert.Null(await GetRegistryRowAsync(childId));
            Assert.False(await SystemContext.IsDatabaseExistingAsync(childId));
        }
        finally
        {
            await DropTenantQuietlyAsync(parent, childId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task AStaleRow_IsNeverDeletedThrough_ButCanBeRemovedOnItsOwn()
    {
        var parentId = NewName("srparent");
        var ghostId = NewName("srghost");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await InsertLocalRowAsync(parent, ghostId, ghostId);
            Assert.Contains(await GetChildrenAsync(parent), t => t.TenantId == ghostId);

            await Assert.ThrowsAsync<TenantException>(() => DropTenantAsync(parent, ghostId));
            await Assert.ThrowsAsync<TenantException>(() => DetachTenantAsync(parent, ghostId));
            Assert.Contains(await GetChildrenAsync(parent), t => t.TenantId == ghostId);

            Assert.True(await RemoveStaleAsync(parent, ghostId));
            Assert.DoesNotContain(await GetChildrenAsync(parent), t => t.TenantId == ghostId);
            Assert.False(await RemoveStaleAsync(parent, ghostId), "idempotent");
        }
        finally
        {
            await DeleteLocalRowsQuietlyAsync(parent, ghostId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task AStaleRow_WhoseIdAndDatabaseWereReusedElsewhere_NeverTouchesTheNewTenant()
    {
        // The real severity of AB#6759: the stale row in the old parent names the id AND the database of a tenant that
        // somebody else created later. Deleting or detaching through the stale row must not drop that tenant's
        // database, unregister it or remove it from its own parent.
        var oldParentId = NewName("ruold");
        var newParentId = NewName("runew");
        var reusedId = NewName("rureused");
        await CreateTenantAsync(SystemContext, oldParentId, oldParentId);
        await CreateTenantAsync(SystemContext, newParentId, newParentId);
        var oldParent = await SystemContext.GetChildTenantContextAsync(oldParentId);
        var newParent = await SystemContext.GetChildTenantContextAsync(newParentId);

        try
        {
            await CreateTenantAsync(newParent, reusedId, reusedId);
            await InsertLocalRowAsync(oldParent, reusedId, reusedId);

            await Assert.ThrowsAsync<TenantException>(() => DropTenantAsync(oldParent, reusedId));
            await Assert.ThrowsAsync<TenantException>(() => DetachTenantAsync(oldParent, reusedId));
            Assert.False(await RemoveStaleAsync(newParent, reusedId), "the new parent's row is live");
            Assert.True(await RemoveStaleAsync(oldParent, reusedId));

            Assert.True(await SystemContext.IsDatabaseExistingAsync(reusedId), "the new tenant's database survives");
            Assert.Equal(newParentId, (await GetRegistryRowAsync(reusedId))?.ParentTenantId);
            Assert.Contains(await GetChildrenAsync(newParent), t => t.TenantId == reusedId);
            Assert.DoesNotContain(await GetChildrenAsync(oldParent), t => t.TenantId == reusedId);
            Assert.NotNull(await SystemContext.TryFindTenantRepositoryAsync(reusedId));
        }
        finally
        {
            await DeleteLocalRowsQuietlyAsync(oldParent, reusedId);
            await DropTenantQuietlyAsync(newParent, reusedId);
            await DropTenantQuietlyAsync(SystemContext, oldParentId);
            await DropTenantQuietlyAsync(SystemContext, newParentId);
        }
    }

    [Fact]
    public async Task Create_PurgesAStaleRowOfAnotherParent()
    {
        var oldParentId = NewName("cpold");
        var newParentId = NewName("cpnew");
        var tenantId = NewName("cptenant");
        await CreateTenantAsync(SystemContext, oldParentId, oldParentId);
        await CreateTenantAsync(SystemContext, newParentId, newParentId);
        var oldParent = await SystemContext.GetChildTenantContextAsync(oldParentId);
        var newParent = await SystemContext.GetChildTenantContextAsync(newParentId);

        try
        {
            await InsertLocalRowAsync(oldParent, tenantId, NewName("cpgone"));

            await CreateTenantAsync(newParent, tenantId, tenantId);

            Assert.DoesNotContain(await GetChildrenAsync(oldParent), t => t.TenantId == tenantId);
            Assert.Single(await GetChildrenAsync(newParent), t => t.TenantId == tenantId);
            Assert.Equal(newParentId, (await GetRegistryRowAsync(tenantId))?.ParentTenantId);
        }
        finally
        {
            await DeleteLocalRowsQuietlyAsync(oldParent, tenantId);
            await DropTenantQuietlyAsync(newParent, tenantId);
            await DropTenantQuietlyAsync(SystemContext, oldParentId);
            await DropTenantQuietlyAsync(SystemContext, newParentId);
        }
    }

    [Fact]
    public async Task Create_BelowTheSameParent_ReplacesItsStaleRow()
    {
        var parentId = NewName("csparent");
        var tenantId = NewName("cstenant");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await InsertLocalRowAsync(parent, tenantId, NewName("csgone"));

            await CreateTenantAsync(parent, tenantId, tenantId);

            var row = Assert.Single(await GetChildrenAsync(parent), t => t.TenantId == tenantId);
            Assert.Equal(tenantId, row.DatabaseName);
        }
        finally
        {
            await DropTenantQuietlyAsync(parent, tenantId);
            await DeleteLocalRowsQuietlyAsync(parent, tenantId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task ALiveTenantThatOnlyLostItsRegistryRow_IsNotStale()
    {
        // AB#6941 state: the parent row and the database exist, the platform registry row is gone. That tenant is
        // live — a create of its id elsewhere is refused instead of orphaning it, and its parent can still delete it.
        var parentId = NewName("lsparent");
        var otherParentId = NewName("lsother");
        var lostId = NewName("lslost");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        await CreateTenantAsync(SystemContext, otherParentId, otherParentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);
        var otherParent = await SystemContext.GetChildTenantContextAsync(otherParentId);

        try
        {
            await CreateTenantAsync(parent, lostId, lostId);
            await RemoveSystemRegistryRowAsync(lostId);

            Assert.False(await RemoveStaleAsync(parent, lostId));
            var refused = await Assert.ThrowsAsync<TenantException>(() =>
                CreateTenantAsync(otherParent, NewName("lsdb"), lostId));
            Assert.True(refused.IsConflict);
            Assert.Contains(await GetChildrenAsync(parent), t => t.TenantId == lostId);

            await DropTenantAsync(parent, lostId);
            Assert.False(await SystemContext.IsDatabaseExistingAsync(lostId));
            Assert.DoesNotContain(await GetChildrenAsync(parent), t => t.TenantId == lostId);
        }
        finally
        {
            await DropTenantQuietlyAsync(parent, lostId);
            await DeleteLocalRowsQuietlyAsync(parent, lostId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
            await DropTenantQuietlyAsync(SystemContext, otherParentId);
        }
    }

    [Fact]
    public async Task Reconcile_ReportsAStaleRow_AndNeverResurrectsIt()
    {
        var parentId = NewName("rcparent");
        var ghostId = NewName("rcghost");
        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await InsertLocalRowAsync(parent, ghostId, ghostId);

            var dryRun = await ReconcileAsync(apply: false);
            var conflict = Assert.Single(dryRun.Conflicts, c => c.TenantId == ghostId);
            Assert.Equal(parentId, conflict.ParentTenantId);
            Assert.StartsWith(Engine.MongoDb.SystemContext.StaleEntryReasonPrefix, conflict.Reason);
            Assert.DoesNotContain(dryRun.Inserted, e => e.TenantId == ghostId);

            var applied = await ReconcileAsync(apply: true);
            Assert.DoesNotContain(applied.Inserted, e => e.TenantId == ghostId);
            Assert.Null(await GetRegistryRowAsync(ghostId));
        }
        finally
        {
            await DeleteLocalRowsQuietlyAsync(parent, ghostId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }
}
