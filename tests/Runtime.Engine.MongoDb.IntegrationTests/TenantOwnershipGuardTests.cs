using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Microsoft.Extensions.Options;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Guards the cross-instance tenant-database ownership marker (AB#4945, Epic AB#4944): a
///     tenant database on a shared MongoDB server must not be attachable by a second OctoMesh
///     instance while this instance owns it. The "second instance" is simulated by writing a
///     marker with a foreign owner system database name — exactly what another instance's
///     create/attach would leave in the database.
/// </summary>
[Collection(TenantNamespaceCollection.Name)]
public class TenantOwnershipGuardTests(TenantNamespaceFixture fixture)
{
    private ISystemContext SystemContext => fixture.GetSystemContext();

    private TenantOwnershipStore OwnershipStore => fixture.GetService<TenantOwnershipStore>();

    private string OwnInstanceIdentity =>
        fixture.GetService<IOptions<OctoSystemConfiguration>>().Value.SystemDatabaseName.Trim().ToLowerInvariant();

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private async Task CreateTenantAsync(string databaseName, string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DetachTenantAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.DetachChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DropTenantQuietlyAsync(string tenantId)
    {
        try
        {
            using var session = await SystemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await SystemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch (Exception)
        {
            // Cleanup only — a tenant that was never created must not mask the assertion failure.
        }
    }

    [Fact]
    public async Task CreateChildTenant_StampsOwnershipMarkerForThisInstance()
    {
        var tenantId = NewName("owncreate");

        await CreateTenantAsync(tenantId, tenantId);
        try
        {
            var marker = await OwnershipStore.GetAsync(tenantId, TestContext.Current.CancellationToken);

            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
            Assert.Equal(tenantId, marker.TenantId);
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task DetachChildTenant_RemovesMarker_AndReattachRestampsIt()
    {
        var tenantId = NewName("handover");

        await CreateTenantAsync(tenantId, tenantId);
        try
        {
            await DetachTenantAsync(tenantId);

            // Detach is the sanctioned ownership handover — the marker must be gone so any
            // instance can adopt the database now.
            Assert.Null(await OwnershipStore.GetAsync(tenantId, TestContext.Current.CancellationToken));

            using (var session = await SystemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var marker = await OwnershipStore.GetAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task AttachChildTenant_DatabaseOwnedByAnotherInstance_ConflictsUniformly()
    {
        var tenantId = NewName("foreign");
        const string foreignInstance = "octosystemotherinstance";

        // A detached database that "another instance" then claimed: exactly the state a second
        // instance's attach leaves behind on the shared server.
        await CreateTenantAsync(tenantId, tenantId);
        try
        {
            await DetachTenantAsync(tenantId);
            await OwnershipStore.StampAsync(tenantId, tenantId, foreignInstance, TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<TenantException>(async () =>
            {
                using var session = await SystemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            });

            // Uniform, reason-free conflict (AB#4763 rule): the caller must not be able to tell
            // "owned by another instance" apart from any other unavailable name. The owner is
            // logged, never returned.
            Assert.True(exception.IsConflict);
            Assert.Contains("is not available", exception.Message);
            Assert.DoesNotContain(foreignInstance, exception.Message);

            // STRICT: nothing was attached and the foreign claim is untouched — takeover only via
            // detach in the owning instance (no force override).
            using (var session = await SystemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var exists = await SystemContext.IsChildTenantExistingAsync(session, tenantId);
                await session.CommitTransactionAsync();
                Assert.False(exists);
            }

            var marker = await OwnershipStore.GetAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(marker);
            Assert.Equal(foreignInstance, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            // Release the simulated foreign claim so the physical database can be cleaned up.
            await OwnershipStore.RemoveAsync(tenantId, TestContext.Current.CancellationToken);
            using (var session = await SystemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task AttachChildTenant_UnstampedLegacyDatabase_IsAdoptedAndStamped()
    {
        var tenantId = NewName("legacy");

        await CreateTenantAsync(tenantId, tenantId);
        try
        {
            await DetachTenantAsync(tenantId);
            // Simulate a database from before the marker shipped: no ownership document at all.
            await OwnershipStore.RemoveAsync(tenantId, TestContext.Current.CancellationToken);

            using (var session = await SystemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var marker = await OwnershipStore.GetAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task TenantResolve_LazilyStampsUnmarkedDatabase()
    {
        var tenantId = NewName("lazystamp");

        await CreateTenantAsync(tenantId, tenantId);
        try
        {
            // Simulate the existing fleet: attached (registry row present) but unstamped.
            await OwnershipStore.RemoveAsync(tenantId, TestContext.Current.CancellationToken);
            TenantContext.ResetServiceManagedCkModelImportGuardForTests();

            var context = await SystemContext.TryGetChildTenantContextAsync(tenantId);
            Assert.NotNull(context);

            var marker = await OwnershipStore.GetAsync(tenantId, TestContext.Current.CancellationToken);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
            Assert.Equal(tenantId, marker.TenantId);
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }
}
