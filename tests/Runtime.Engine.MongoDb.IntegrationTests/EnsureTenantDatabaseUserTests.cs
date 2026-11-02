using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8177 — the MongoDB user of a tenant database is created only on tenant create/attach. When it is gone
///     (restored or re-created MongoDB, manual cleanup) the tenant fails with an authentication error forever.
///     <see cref="ISystemContext.EnsureTenantDatabaseUserAsync" /> is what the setup retry calls to heal it.
/// </summary>
[Collection(SystemCollection.Name)]
public class EnsureTenantDatabaseUserTests(SystemFixture fixture)
{
    private static string NewTenantId() => $"eu-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task A_dropped_tenant_user_is_recreated_and_the_tenant_database_is_usable_again()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId();
        var databaseName = $"db-{tenantId}";
        await CreateTenantAsync(systemContext, tenantId, databaseName);
        try
        {
            await DropTenantUserAsync(databaseName);

            // The condition of the bug: the tenant's own user no longer authenticates.
            var broken = await Assert.ThrowsAnyAsync<Exception>(() => ReadClientsAsync(systemContext, tenantId, databaseName));
            Assert.Contains("authenticat", broken.ToString(), StringComparison.OrdinalIgnoreCase);

            Assert.Equal(TenantDatabaseUserEnsureResult.Created,
                await systemContext.EnsureTenantDatabaseUserAsync(tenantId));

            await ReadClientsAsync(systemContext, tenantId, databaseName);

            // Idempotent: the second call finds the user.
            Assert.Equal(TenantDatabaseUserEnsureResult.AlreadyPresent,
                await systemContext.EnsureTenantDatabaseUserAsync(tenantId));
        }
        finally
        {
            await DeleteTenantAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task An_unregistered_tenant_is_left_alone()
    {
        var systemContext = fixture.GetSystemContext();

        Assert.Equal(TenantDatabaseUserEnsureResult.TenantNotRegistered,
            await systemContext.EnsureTenantDatabaseUserAsync(NewTenantId()));
    }

    private async Task DropTenantUserAsync(string databaseName)
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var admin = fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName);
        await admin.DropUser(config.AuthenticationDatabaseName, string.Format(config.DatabaseUser, databaseName));
        // Cached user connections of the tenant would still be authenticated.
        fixture.GetService<IUserRepositoryAccess>().Invalidate(databaseName);
    }

    private static async Task ReadClientsAsync(ISystemContext systemContext, string tenantId, string databaseName)
    {
        var repository = systemContext.GetRegisteredTenantRepository(new OctoTenant(tenantId, databaseName));
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.GetRtEntitiesByTypeAsync<Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2.RtTenant>(
            session, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
    }

    private static async Task CreateTenantAsync(ISystemContext systemContext, string tenantId, string databaseName)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task DeleteTenantAsync(ISystemContext systemContext, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var handle = await systemContext.DeleteChildTenantMetadataAsync(session, tenantId);
        await session.CommitTransactionAsync();
        await systemContext.DropTenantDatabaseAsync(handle, tenantId);
    }
}
