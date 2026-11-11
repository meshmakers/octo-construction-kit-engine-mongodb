using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Deleting a tenant must never drop a database that belongs to another OctoMesh instance on the same MongoDB
///     server (AB#8807). Instance A is the fixture, instance B its <see cref="TwoInstanceFixture.Other" /> with its
///     own system database on the same server. The double registrations are written straight into A's registry:
///     that is the state a double attach from before the ownership marker (AB#4945) left behind; the attach guards
///     no longer let one through.
/// </summary>
[Collection(TwoInstanceCollection.Name)]
public class DeleteOwnershipGuardTests(TwoInstanceFixture fixture)
{
    private ISystemContext InstanceA => fixture.GetSystemContext();

    private ISystemContext InstanceB => fixture.Other.GetSystemContext();

    private string SystemDatabaseA => fixture.SystemDatabaseName.ToLowerInvariant();

    private string SystemDatabaseB => fixture.Other.SystemDatabaseName.ToLowerInvariant();

    private TenantOwnershipStore OwnershipStore => fixture.GetService<TenantOwnershipStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    [Fact]
    public async Task Delete_OfADatabaseWhoseMarkerNamesAnotherInstance_IsRefusedUniformly_AndKeepsTheDatabase()
    {
        var tenantOfB = NewName("dgmarked");
        var doubleIdA = NewName("dgdoublea");

        await CreateAsync(InstanceB, tenantOfB);
        try
        {
            await RegisterDirectlyAsync(InstanceA, doubleIdA, tenantOfB);

            var exception = await Assert.ThrowsAsync<TenantException>(() => DeleteAsync(InstanceA, doubleIdA));

            AssertUniformConflict(exception, tenantOfB);
            AssertRefusalLogged(tenantOfB, SystemDatabaseB);
            await AssertUntouchedAsync(doubleIdA, tenantOfB, SystemDatabaseB);
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(tenantOfB));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, doubleIdA);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
        }
    }

    [Fact]
    public async Task DeleteMetadata_OfAnUnmarkedDatabaseAnotherInstanceRegisters_IsRefusedBeforeAnyRecordIsDeleted()
    {
        // A tenant of B from before the marker shipped: only B's registry says it is B's. The REST delete calls the
        // first phase directly, so the guard has to sit there.
        var tenantOfB = NewName("dglegacy");
        var doubleIdA = NewName("dglegacya");

        await CreateAsync(InstanceB, tenantOfB);
        try
        {
            await OwnershipStore.RemoveAsync(tenantOfB, Ct);
            await RegisterDirectlyAsync(InstanceA, doubleIdA, tenantOfB);

            var exception = await Assert.ThrowsAsync<TenantException>(() =>
                InTransactionAsync(InstanceA, s => InstanceA.DeleteChildTenantMetadataAsync(s, doubleIdA, true)));

            AssertUniformConflict(exception, tenantOfB);
            AssertRefusalLogged(tenantOfB, SystemDatabaseB);
            Assert.True(await IsChildAsync(InstanceA, doubleIdA));
            Assert.True(await InstanceA.IsDatabaseExistingAsync(tenantOfB));
            Assert.Null(await OwnershipStore.GetAsync(tenantOfB, Ct));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, doubleIdA);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
        }
    }

    [Fact]
    public async Task Delete_OfOwnDatabaseThatAnotherInstanceAlsoRegisters_IsRefused()
    {
        // The owner side of a double registration: B may still work on the database, so A detaches first.
        var tenantOfA = NewName("dgowned");
        var doubleIdB = NewName("dgdoubleb");

        await CreateAsync(InstanceA, tenantOfA);
        try
        {
            await RegisterDirectlyAsync(InstanceB, doubleIdB, tenantOfA);

            var exception = await Assert.ThrowsAsync<TenantException>(() => DeleteAsync(InstanceA, tenantOfA));

            AssertUniformConflict(exception, tenantOfA);
            AssertRefusalLogged(tenantOfA, SystemDatabaseB);
            await AssertUntouchedAsync(tenantOfA, tenantOfA, SystemDatabaseA);
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceB, doubleIdB);
            await ThrowawayTenant.DropAsync(InstanceA, tenantOfA);
        }
    }

    [Fact]
    public async Task Clear_OfADatabaseWhoseMarkerNamesAnotherInstance_IsRefused()
    {
        // Clear drops the database and re-creates it empty: the same destruction as a delete.
        var tenantOfB = NewName("dgclear");
        var doubleIdA = NewName("dgcleara");

        await CreateAsync(InstanceB, tenantOfB);
        try
        {
            await RegisterDirectlyAsync(InstanceA, doubleIdA, tenantOfB);

            var exception = await Assert.ThrowsAsync<TenantException>(() =>
                InTransactionAsync(InstanceA, s => InstanceA.ClearChildTenantAsync(s, doubleIdA)));

            AssertUniformConflict(exception, tenantOfB);
            await AssertUntouchedAsync(doubleIdA, tenantOfB, SystemDatabaseB);
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, doubleIdA);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
        }
    }

    [Fact]
    public async Task Delete_OfOwnTenant_DropsTheDatabase_AsBefore()
    {
        var tenantOfA = NewName("dgown");

        await CreateAsync(InstanceA, tenantOfA);
        try
        {
            await DeleteAsync(InstanceA, tenantOfA);

            Assert.False(await IsChildAsync(InstanceA, tenantOfA));
            Assert.False(await InstanceA.IsDatabaseExistingAsync(tenantOfA));
            Assert.DoesNotContain(fixture.Logs.Entries, e =>
                e.Message.Contains("OWNERSHIP OVERRIDE", StringComparison.Ordinal) &&
                e.Message.Contains(tenantOfA, StringComparison.Ordinal));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, tenantOfA);
        }
    }

    [Fact]
    public async Task Delete_OfOwnUnmarkedTenant_DropsTheDatabase_AsBefore()
    {
        // A tenant of A from before the marker: only A registers it, so it is A's.
        var tenantOfA = NewName("dgownlegacy");

        await CreateAsync(InstanceA, tenantOfA);
        try
        {
            await OwnershipStore.RemoveAsync(tenantOfA, Ct);

            await DeleteAsync(InstanceA, tenantOfA);

            Assert.False(await IsChildAsync(InstanceA, tenantOfA));
            Assert.False(await InstanceA.IsDatabaseExistingAsync(tenantOfA));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, tenantOfA);
        }
    }

    [Fact]
    public async Task Delete_OfOwnUnmarkedParentTenant_WhoseRegistryListsItsChild_DropsTheDatabase_AsBefore()
    {
        // The registry rows of a parent tenant name it as parent: such a database is not taken for the system
        // database of another instance.
        var parentId = NewName("dgparent");
        var childId = NewName("dgchild");

        await CreateAsync(InstanceA, parentId);
        try
        {
            var parent = await InstanceA.TryFindTenantContextAsync(parentId);
            Assert.NotNull(parent);
            await CreateAsync(parent!, childId);
            await OwnershipStore.RemoveAsync(parentId, Ct);

            await DeleteAsync(InstanceA, parentId);

            Assert.False(await IsChildAsync(InstanceA, parentId));
            Assert.False(await InstanceA.IsDatabaseExistingAsync(parentId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, childId);
            await ThrowawayTenant.DropAsync(InstanceA, parentId);
        }
    }

    [Fact]
    public async Task Delete_OfADatabaseWithAnOrphanedForeignMarker_IsRefused_AndWithOverrideDropsIt_AndLogsTheOverride()
    {
        // The marker names an instance that registers nothing any more (gone, or detached without releasing its
        // claim): the one case the deliberate override is for.
        const string goneInstance = "octosystemgone";
        var tenantOfA = NewName("dgorphan");

        await CreateAsync(InstanceA, tenantOfA);
        try
        {
            await OwnershipStore.StampAsync(tenantOfA, tenantOfA, goneInstance, Ct);

            var exception = await Assert.ThrowsAsync<TenantException>(() => DeleteAsync(InstanceA, tenantOfA));
            AssertUniformConflict(exception, tenantOfA);
            AssertRefusalLogged(tenantOfA, goneInstance);
            await AssertUntouchedAsync(tenantOfA, tenantOfA, goneInstance);

            await InTransactionAsync(InstanceA,
                s => InstanceA.DropChildTenantAsync(s, tenantOfA, overrideForeignOwnership: true));

            Assert.False(await IsChildAsync(InstanceA, tenantOfA));
            Assert.False(await InstanceA.IsDatabaseExistingAsync(tenantOfA));
            Assert.Contains(fixture.Logs.Entries, e =>
                e.Level == LogLevel.Warning &&
                e.Message.StartsWith("OWNERSHIP OVERRIDE", StringComparison.Ordinal) &&
                e.Message.Contains(tenantOfA, StringComparison.Ordinal) &&
                e.Message.Contains(goneInstance, StringComparison.Ordinal));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, tenantOfA, overrideForeignOwnership: true);
        }
    }

    [Fact]
    public async Task Delete_WithOverride_OfADatabaseAnotherLiveInstanceRegisters_IsStillRefused()
    {
        // B registers the database and may be working on it: the override never drops it.
        var tenantOfB = NewName("dglive");
        var doubleIdA = NewName("dglivea");

        await CreateAsync(InstanceB, tenantOfB);
        try
        {
            await RegisterDirectlyAsync(InstanceA, doubleIdA, tenantOfB);

            var exception = await Assert.ThrowsAsync<TenantException>(() =>
                InTransactionAsync(InstanceA,
                    s => InstanceA.DropChildTenantAsync(s, doubleIdA, overrideForeignOwnership: true)));

            AssertUniformConflict(exception, tenantOfB);
            Assert.Contains(fixture.Logs.Entries, e =>
                e.Message.Contains(doubleIdA, StringComparison.Ordinal) &&
                e.Message.Contains("cannot be overridden while that instance registers it", StringComparison.Ordinal));
            await AssertUntouchedAsync(doubleIdA, tenantOfB, SystemDatabaseB);
            Assert.DoesNotContain(fixture.Logs.Entries, e =>
                e.Message.StartsWith("OWNERSHIP OVERRIDE", StringComparison.Ordinal) &&
                e.Message.Contains(doubleIdA, StringComparison.Ordinal));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, doubleIdA);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
        }
    }

    [Fact]
    public async Task Delete_OfTheSystemDatabaseOfAnotherInstance_IsRefused_EvenWithOverride()
    {
        // A system database is recognised by its tenant registry rows, so B needs one tenant.
        var tenantOfB = NewName("dgtenantb");
        var corruptIdA = NewName("dgsysdb");

        await CreateAsync(InstanceB, tenantOfB);
        try
        {
            await RegisterDirectlyAsync(InstanceA, corruptIdA, SystemDatabaseB);

            var exception = await Assert.ThrowsAsync<TenantException>(() =>
                InTransactionAsync(InstanceA,
                    s => InstanceA.DropChildTenantAsync(s, corruptIdA, overrideForeignOwnership: true)));

            AssertUniformConflict(exception, SystemDatabaseB);
            Assert.Contains(fixture.Logs.Entries, e =>
                e.Message.Contains(corruptIdA, StringComparison.Ordinal) &&
                e.Message.Contains("cannot be overridden", StringComparison.Ordinal));
            Assert.True(await InstanceB.IsSystemTenantExistingAsync());
            Assert.NotNull(await InstanceB.TryFindTenantContextAsync(tenantOfB));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, corruptIdA);
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
        }
    }

    /// <summary>
    ///     The uniform conflict of AB#4763: the same text as every other unavailable database name, nothing about
    ///     the owner.
    /// </summary>
    private static void AssertUniformConflict(TenantException exception, string databaseName)
    {
        Assert.True(exception.IsConflict);
        Assert.Equal(TenantException.DatabaseNameNotAvailable(databaseName).Message, exception.Message);
    }

    /// <summary>The owner is in A's server log.</summary>
    private void AssertRefusalLogged(string databaseName, string ownerSystemDatabase)
    {
        Assert.Contains(fixture.Logs.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains($"Rejected database name '{databaseName}'", StringComparison.Ordinal) &&
            e.Message.Contains("the delete would drop", StringComparison.Ordinal) &&
            e.Message.Contains(ownerSystemDatabase, StringComparison.Ordinal));
    }

    /// <summary>A refused delete changed nothing: A's row, the database and its marker are as before.</summary>
    private async Task AssertUntouchedAsync(string tenantIdInA, string databaseName, string markerOwner)
    {
        Assert.True(await IsChildAsync(InstanceA, tenantIdInA));
        Assert.True(await InstanceA.IsDatabaseExistingAsync(databaseName));
        var marker = await OwnershipStore.GetAsync(databaseName, Ct);
        Assert.NotNull(marker);
        Assert.Equal(markerOwner, marker!.OwnerSystemDatabaseName);
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

    private static Task DeleteAsync(ITenantContext context, string tenantId) =>
        InTransactionAsync(context, s => context.DropChildTenantAsync(s, tenantId, true));

    private static async Task<bool> IsChildAsync(ITenantContext context, string tenantId)
    {
        using var session = await context.GetAdminSessionAsync();
        session.StartTransaction();
        var existing = await context.IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return existing;
    }

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
}
