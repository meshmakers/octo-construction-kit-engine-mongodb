using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Unambiguous tenant-database ownership between two OctoMesh instances on one MongoDB server
///     (AB#8796): instance A is the fixture, instance B its <see cref="TwoInstanceFixture.Other" />.
///     Covers the attach guard for databases without a marker, the resolve-time warning with the
///     Degraded health state, and the idempotent list/stamp operation.
/// </summary>
[Collection(TwoInstanceCollection.Name)]
public class TenantOwnershipCrossInstanceTests(TwoInstanceFixture fixture)
{
    private ISystemContext InstanceA => fixture.GetSystemContext();

    private ISystemContext InstanceB => fixture.Other.GetSystemContext();

    private TenantOwnershipStore OwnershipStore => fixture.GetService<TenantOwnershipStore>();

    private string IdentityA =>
        fixture.GetService<IOptions<OctoSystemConfiguration>>().Value.SystemDatabaseName.Trim().ToLowerInvariant();

    private string IdentityB => fixture.Other.SystemDatabaseName.Trim().ToLowerInvariant();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static async Task InTransactionAsync(ITenantContext context, Func<IOctoAdminSession, Task> action)
    {
        using var session = await context.GetAdminSessionAsync();
        session.StartTransaction();
        await action(session);
        await session.CommitTransactionAsync();
    }

    private static Task CreateAsync(ITenantContext context, string tenantId) =>
        InTransactionAsync(context, s => context.CreateChildTenantAsync(s, tenantId, tenantId));

    private static Task AttachAsync(ITenantContext context, string databaseName, string tenantId) =>
        InTransactionAsync(context, s => context.AttachChildTenantAsync(s, databaseName, tenantId));

    /// <summary>
    ///     Writes a registry row straight into an instance's system registry — the state a double attach
    ///     from before the ownership marker (AB#4945) left behind; the guards no longer let one through.
    /// </summary>
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

    private async Task<bool> IsRegisteredAsync(ISystemContext instance, string tenantId)
    {
        using var session = await instance.GetAdminSessionAsync();
        session.StartTransaction();
        var exists = await instance.IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return exists;
    }

    private static async Task<TenantOwnershipReviewResult> ReviewAsync(ISystemContext instance, bool stamp)
    {
        TenantOwnershipReviewResult? result = null;
        await InTransactionAsync(instance, async s => result = await instance.ReviewTenantOwnershipAsync(s, stamp));
        return result!;
    }

    [Fact]
    public async Task Attach_UnmarkedDatabaseRegisteredByOtherInstance_IsRefusedUniformly()
    {
        var tenantId = NewName("unmarkedb");
        await CreateAsync(InstanceB, tenantId);
        try
        {
            // A database of B from before the marker shipped that B has not resolved since.
            await OwnershipStore.RemoveAsync(tenantId, Ct);

            var exception = await Assert.ThrowsAsync<TenantException>(() => AttachAsync(InstanceA, tenantId, tenantId));

            // Same uniform conflict as every other unavailable name (AB#4763); the owning system
            // database goes to the log only.
            Assert.True(exception.IsConflict);
            Assert.Contains("is not available", exception.Message);
            Assert.DoesNotContain(IdentityB, exception.Message);
            Assert.Contains(fixture.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(IdentityB) &&
                                                       e.Message.Contains(tenantId));

            // Nothing changed: A has no row, the database still has no marker, B still has its tenant.
            Assert.False(await IsRegisteredAsync(InstanceA, tenantId));
            Assert.Null(await OwnershipStore.GetAsync(tenantId, Ct));
            Assert.True(await IsRegisteredAsync(InstanceB, tenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantId);
        }
    }

    [Fact]
    public async Task Attach_UnmarkedDatabaseNoSystemDatabaseRegisters_IsAdoptedAndStamped()
    {
        var tenantId = NewName("freeunmarked");
        await CreateAsync(InstanceB, tenantId);
        try
        {
            // B released it (detach removes B's marker); legacy: no marker at all.
            await InTransactionAsync(InstanceB, s => InstanceB.DetachChildTenantAsync(s, tenantId));
            Assert.Null(await OwnershipStore.GetAsync(tenantId, Ct));

            await AttachAsync(InstanceA, tenantId, tenantId);

            var marker = await OwnershipStore.GetAsync(tenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(IdentityA, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, tenantId);
        }
    }

    [Fact]
    public async Task Attach_SystemDatabaseOfOtherInstance_IsRefused()
    {
        // A system database is recognised by its tenant registry, so B needs at least one tenant.
        var tenantIdB = NewName("sysregb");
        var tenantId = NewName("grabsystem");
        await CreateAsync(InstanceB, tenantIdB);
        try
        {
            var exception = await Assert.ThrowsAsync<TenantException>(() => AttachAsync(InstanceA, IdentityB, tenantId));

            // The requested name is echoed back as in every rejection; the reason is not.
            Assert.True(exception.IsConflict);
            Assert.Contains("is not available", exception.Message);
            Assert.DoesNotContain("system database", exception.Message);
            Assert.False(await IsRegisteredAsync(InstanceA, tenantId));
            Assert.Null(await OwnershipStore.GetAsync(IdentityB, Ct));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantIdB);
        }
    }

    [Fact]
    public async Task Resolve_DatabaseMarkedByOtherInstance_WarnsOnce_DegradesHealth_AndKeepsWorking()
    {
        var tenantIdA = NewName("splitbraina");
        var tenantIdB = NewName("splitbrainb");
        var databaseName = tenantIdB;
        await CreateAsync(InstanceB, tenantIdB);
        try
        {
            // Pre-AB#4945 double registration: A holds a row for B's database; B's marker is on it.
            await RegisterDirectlyAsync(InstanceA, tenantIdA, databaseName);
            InstanceA.InvalidateTenantResolveImportGuards(tenantIdA);

            var context = await InstanceA.TryGetChildTenantContextAsync(tenantIdA);
            Assert.NotNull(context);
            // Resolve keeps working and warns once per process and tenant.
            Assert.NotNull(await InstanceA.TryGetChildTenantContextAsync(tenantIdA));
            Assert.NotNull(await InstanceA.TryGetChildTenantContextAsync(tenantIdA));

            // The marker is never overwritten.
            var marker = await OwnershipStore.GetAsync(databaseName, Ct);
            Assert.Equal(IdentityB, marker!.OwnerSystemDatabaseName);

            var warnings = fixture.Logs.Entries.Where(e =>
                e.Level == LogLevel.Warning && e.Message.Contains($"Tenant '{tenantIdA}' resolves to database")).ToList();
            var warning = Assert.Single(warnings);
            Assert.Contains(databaseName, warning.Message);
            Assert.Contains(IdentityB, warning.Message);

            var conflict = Assert.Single(fixture.GetService<ITenantOwnershipConflicts>().Current,
                c => c.TenantId == tenantIdA);
            Assert.Equal(databaseName, conflict.DatabaseName);
            Assert.Equal(IdentityB, conflict.OwnerSystemDatabaseName);

            // The health check is built by DI exactly as AddSystemContextHealthCheck does it.
            var healthCheck = ActivatorUtilities.CreateInstance<TenantContextHealthCheck>(fixture.Provider!);
            var health = await healthCheck.CheckHealthAsync(new HealthCheckContext(), Ct);
            Assert.Equal(HealthStatus.Degraded, health.Status);
            Assert.Contains(tenantIdA, health.Description);
            Assert.Contains(databaseName, health.Description);
            Assert.Contains(IdentityB, health.Description);

            // Resolving it in B, the owner, raises nothing.
            InstanceB.InvalidateTenantResolveImportGuards(tenantIdB);
            Assert.NotNull(await InstanceB.TryGetChildTenantContextAsync(tenantIdB));
            Assert.DoesNotContain(fixture.Other.GetService<ITenantOwnershipConflicts>().Current,
                c => c.TenantId == tenantIdB);

            // The documented way out: detach in the instance that does not own the database. It must
            // leave the owner's marker alone and clear the conflict.
            await InTransactionAsync(InstanceA, s => InstanceA.DetachChildTenantAsync(s, tenantIdA));
            Assert.Equal(IdentityB, (await OwnershipStore.GetAsync(databaseName, Ct))!.OwnerSystemDatabaseName);
            Assert.DoesNotContain(fixture.GetService<ITenantOwnershipConflicts>().Current, c => c.TenantId == tenantIdA);
            Assert.True(await IsRegisteredAsync(InstanceB, tenantIdB));
        }
        finally
        {
            await UnregisterDirectlyQuietlyAsync(InstanceA, tenantIdA);
            InstanceA.InvalidateTenantResolveImportGuards(tenantIdA);
            await ThrowawayTenant.DropAsync(InstanceB, tenantIdB);
        }
    }

    [Fact]
    public async Task Review_ListsWithoutWriting_StampsOnlyUnambiguous_AndIsIdempotent()
    {
        var unmarkedId = NewName("revunmarked");
        var foreignId = NewName("revforeign");
        var doubleIdA = NewName("revdoublea");
        var doubleIdB = NewName("revdoubleb");
        var ownedId = NewName("revowned");
        await CreateAsync(InstanceA, unmarkedId);
        await CreateAsync(InstanceA, foreignId);
        await CreateAsync(InstanceA, ownedId);
        await CreateAsync(InstanceB, doubleIdB);
        try
        {
            await OwnershipStore.RemoveAsync(unmarkedId, Ct);
            await OwnershipStore.StampAsync(foreignId, foreignId, IdentityB, Ct);
            // B's database registered in A as well, with B's marker removed: unmarked AND ambiguous.
            await RegisterDirectlyAsync(InstanceA, doubleIdA, doubleIdB);
            await OwnershipStore.RemoveAsync(doubleIdB, Ct);

            var mine = new HashSet<string> { unmarkedId, foreignId, doubleIdA, ownedId };

            // List only: nothing is written.
            var listed = await ReviewAsync(InstanceA, stamp: false);
            Assert.False(listed.Stamp);
            var listedEntries = listed.Entries.Where(e => mine.Contains(e.TenantId)).ToDictionary(e => e.TenantId);
            Assert.Equal(3, listedEntries.Count);
            Assert.Equal(TenantOwnershipIssue.Unmarked, listedEntries[unmarkedId].Issue);
            Assert.False(listedEntries[unmarkedId].Stamped);
            Assert.Equal(TenantOwnershipIssue.MarkedByOtherInstance, listedEntries[foreignId].Issue);
            Assert.Equal(IdentityB, listedEntries[foreignId].MarkerOwnerSystemDatabaseName);
            Assert.Equal(TenantOwnershipIssue.RegisteredByOtherInstance, listedEntries[doubleIdA].Issue);
            Assert.Equal(new[] { IdentityA, IdentityB }.OrderBy(n => n, StringComparer.Ordinal),
                listedEntries[doubleIdA].RegisteringSystemDatabases);
            Assert.Null(await OwnershipStore.GetAsync(unmarkedId, Ct));
            Assert.Null(await OwnershipStore.GetAsync(doubleIdB, Ct));

            // Stamp: only the unambiguous unmarked database gets A's marker.
            var stamped = await ReviewAsync(InstanceA, stamp: true);
            var stampedEntries = stamped.Entries.Where(e => mine.Contains(e.TenantId)).ToDictionary(e => e.TenantId);
            Assert.True(stampedEntries[unmarkedId].Stamped);
            Assert.False(stampedEntries[foreignId].Stamped);
            Assert.False(stampedEntries[doubleIdA].Stamped);
            Assert.Equal(IdentityA, (await OwnershipStore.GetAsync(unmarkedId, Ct))!.OwnerSystemDatabaseName);
            Assert.Equal(IdentityB, (await OwnershipStore.GetAsync(foreignId, Ct))!.OwnerSystemDatabaseName);
            Assert.Null(await OwnershipStore.GetAsync(doubleIdB, Ct));

            // Again: nothing new is stamped, the ambiguous databases are listed exactly as before.
            var again = await ReviewAsync(InstanceA, stamp: true);
            var againEntries = again.Entries.Where(e => mine.Contains(e.TenantId)).ToList();
            Assert.DoesNotContain(againEntries, e => e.Stamped);
            Assert.Equal(
                stampedEntries.Values.Where(e => e.TenantId != unmarkedId).OrderBy(e => e.TenantId)
                    .Select(e => (e.TenantId, e.Issue, e.MarkerOwnerSystemDatabaseName,
                        string.Join(",", e.RegisteringSystemDatabases))),
                againEntries.OrderBy(e => e.TenantId)
                    .Select(e => (e.TenantId, e.Issue, e.MarkerOwnerSystemDatabaseName,
                        string.Join(",", e.RegisteringSystemDatabases))));
            Assert.Null(await OwnershipStore.GetAsync(doubleIdB, Ct));
        }
        finally
        {
            await OwnershipStore.RemoveAsync(foreignId, Ct);
            await UnregisterDirectlyQuietlyAsync(InstanceA, doubleIdA);
            await ThrowawayTenant.DropAsync(InstanceA, unmarkedId);
            await ThrowawayTenant.DropAsync(InstanceA, foreignId);
            await ThrowawayTenant.DropAsync(InstanceA, ownedId);
            await ThrowawayTenant.DropAsync(InstanceB, doubleIdB);
        }
    }
}
