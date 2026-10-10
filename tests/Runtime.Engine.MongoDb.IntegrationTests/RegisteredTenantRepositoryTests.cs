using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#6308 — <see cref="ISystemContext.GetRegisteredTenantRepository" />: repository access for a tenant
///     the caller already knows from the registry, without the existence probe, the registry lookup, an admin
///     session or any CK auto-import that <see cref="ISystemContext.FindTenantRepositoryAsync" /> performs.
/// </summary>
/// <remarks>
///     The proof of "no I/O" is a tenant that does not exist at all: the call must still hand out a
///     repository, whereas the resolving route has to fail on the same input.
/// </remarks>
[Collection(SystemCollection.Name)]
public class RegisteredTenantRepositoryTests(SystemFixture fixture)
{
    private static string NewTenantId() => $"rt-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Repository_is_handed_out_for_a_tenant_without_database_or_registry_row()
    {
        var systemContext = fixture.GetSystemContext();
        var ghost = new OctoTenant(NewTenantId(), $"db-{Guid.NewGuid():N}"[..20]);

        // The resolving route proves the baseline: the tenant is unknown, so it fails ...
        await Assert.ThrowsAnyAsync<Exception>(() => systemContext.FindTenantRepositoryAsync(ghost.TenantId));

        // ... while the lightweight route trusts the registry entry and touches nothing.
        var repository = systemContext.GetRegisteredTenantRepository(ghost);

        Assert.NotNull(repository);
        Assert.Equal(ghost.TenantId, repository.TenantId);
    }

    [Fact]
    public async Task Repository_of_a_registered_tenant_is_usable_without_resolving_the_tenant()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId();
        var databaseName = $"db-{tenantId}";

        await CreateTenantAsync(systemContext, tenantId, databaseName);
        TenantDeletionHandle? handle = null;
        try
        {
            OctoTenant registered;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                var all = await systemContext.GetAllTenantsAsync(session);
                registered = all.Items.Single(t => t.TenantId == tenantId);
            }

            var repository = systemContext.GetRegisteredTenantRepository(registered);

            Assert.Equal(tenantId, repository.TenantId);
            using var tenantSession = await repository.GetSessionAsync();
            Assert.NotNull(tenantSession);
        }
        finally
        {
            handle = await DeleteTenantAsync(systemContext, tenantId);
            await systemContext.DropTenantDatabaseAsync(handle, tenantId);
        }
    }

    [Fact]
    public void System_tenant_resolves_to_the_system_database_repository()
    {
        var systemContext = fixture.GetSystemContext();

        var repository = systemContext.GetRegisteredTenantRepository(
            new OctoTenant(systemContext.TenantId, systemContext.DatabaseName));

        Assert.Equal(systemContext.TenantId, repository.TenantId);
    }

    [Fact]
    public void Null_tenant_is_rejected()
    {
        var systemContext = fixture.GetSystemContext();

        Assert.Throws<ArgumentNullException>(() => systemContext.GetRegisteredTenantRepository(null!));
    }

    private static async Task CreateTenantAsync(ISystemContext systemContext, string tenantId, string databaseName)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task<TenantDeletionHandle> DeleteTenantAsync(ISystemContext systemContext, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var handle = await systemContext.DeleteChildTenantMetadataAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return handle;
    }
}
