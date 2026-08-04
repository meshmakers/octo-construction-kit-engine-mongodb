using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// AB#4690 — the admin and user repository clients are cached per database name for the lifetime of the
/// process. Dropping a tenant also drops its database user, which invalidates the authentication of every
/// connection already open in those pools; the MongoDB driver never re-authenticates an existing
/// connection, so each one keeps failing with error 13 ("... requires authentication") even after the
/// tenant is re-created and the user exists again. That is what left a re-created tenant unusable until
/// the process was restarted, so the caches must be dropped when a tenant goes away.
/// </summary>
[Collection(SystemCollection.Name)]
public class RepositoryClientInvalidationTests(SystemFixture fixture)
{
    [Fact]
    public void Invalidate_ForcesAFreshAdminClient()
    {
        // A database name nothing else touches — Invalidate disposes the client it evicts.
        var databaseName = $"invtest{Guid.NewGuid():N}"[..20];
        var access = fixture.GetService<IAdminRepositoryAccess>();

        var first = access.GetRepositoryClient(databaseName);
        Assert.Same(first, access.GetRepositoryClient(databaseName));

        access.Invalidate(databaseName);

        Assert.NotSame(first, access.GetRepositoryClient(databaseName));
        access.Invalidate(databaseName);
    }

    [Fact]
    public void Invalidate_ForcesAFreshUserClient()
    {
        var databaseName = $"invtest{Guid.NewGuid():N}"[..20];
        var access = fixture.GetService<IUserRepositoryAccess>();

        var first = access.GetRepositoryClient(databaseName);
        Assert.Same(first, access.GetRepositoryClient(databaseName));

        access.Invalidate(databaseName);

        Assert.NotSame(first, access.GetRepositoryClient(databaseName));
        access.Invalidate(databaseName);
    }

    [Fact]
    public void Invalidate_IsANoOp_ForAnUnknownDatabase()
    {
        var access = fixture.GetService<IAdminRepositoryAccess>();

        // Must not throw — the tenant lifecycle events call this unconditionally.
        access.Invalidate($"invtest{Guid.NewGuid():N}"[..20]);
    }

    [Fact]
    public async Task InvalidateTenantRepositoryClients_DropsBothCaches_ForAnExplicitDatabaseName()
    {
        var ct = TestContext.Current.CancellationToken;
        var databaseName = $"invtest{Guid.NewGuid():N}"[..20];
        var systemContext = fixture.GetService<ISystemContext>();
        var adminAccess = fixture.GetService<IAdminRepositoryAccess>();
        var userAccess = fixture.GetService<IUserRepositoryAccess>();

        var admin = adminAccess.GetRepositoryClient(databaseName);
        var user = userAccess.GetRepositoryClient(databaseName);

        // The database name is passed explicitly because the delete path calls this after the tenant
        // record is gone, when it can no longer be resolved.
        await systemContext.InvalidateTenantRepositoryClientsAsync("tenant-does-not-exist", databaseName, ct);

        Assert.NotSame(admin, adminAccess.GetRepositoryClient(databaseName));
        Assert.NotSame(user, userAccess.GetRepositoryClient(databaseName));

        adminAccess.Invalidate(databaseName);
        userAccess.Invalidate(databaseName);
    }

    [Fact]
    public async Task InvalidateTenantRepositoryClients_IsANoOp_WhenTheDatabaseNameCannotBeResolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var systemContext = fixture.GetService<ISystemContext>();

        // No record, no explicit name — must degrade quietly rather than throw: the lifecycle consumers
        // call this on every tenant delete / create, including for tenants this process never saw.
        await systemContext.InvalidateTenantRepositoryClientsAsync($"nope-{Guid.NewGuid():N}"[..20],
            cancellationToken: ct);
    }
}
