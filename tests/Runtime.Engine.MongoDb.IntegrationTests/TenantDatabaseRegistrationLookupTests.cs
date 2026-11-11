using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The cross-instance registration lookup (AB#8796): which system databases on the server
///     register a tenant database, and which databases are system databases at all. Instance A is
///     the fixture, instance B its <see cref="TwoInstanceFixture.Other" /> on the same server.
/// </summary>
[Collection(TwoInstanceCollection.Name)]
public class TenantDatabaseRegistrationLookupTests(TwoInstanceFixture fixture)
{
    private TenantDatabaseRegistrationLookup Lookup => fixture.GetService<TenantDatabaseRegistrationLookup>();

    private string SystemDatabaseA => fixture.SystemDatabaseName.ToLowerInvariant();

    private string SystemDatabaseB => fixture.Other.SystemDatabaseName.ToLowerInvariant();

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static async Task CreateTenantAsync(ITenantContext parent, string databaseName, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task TenantOfOtherInstance_IsRegisteredOnlyInThatSystemDatabase()
    {
        var tenantId = NewName("lookupb");
        await CreateTenantAsync(fixture.Other.GetSystemContext(), tenantId, tenantId);
        try
        {
            var registering = await Lookup.FindRegisteringSystemDatabasesAsync(tenantId,
                TestContext.Current.CancellationToken);

            Assert.Equal([SystemDatabaseB], registering);
            Assert.True(await Lookup.IsSystemDatabaseAsync(SystemDatabaseB, TestContext.Current.CancellationToken));
            Assert.False(await Lookup.IsSystemDatabaseAsync(tenantId, TestContext.Current.CancellationToken));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(fixture.Other.GetSystemContext(), tenantId);
        }
    }

    [Fact]
    public async Task NestedTenant_ParentWithChildRows_IsNotASystemDatabase()
    {
        var parentId = NewName("lookupparent");
        var childId = NewName("lookupchild");
        var systemContext = fixture.Other.GetSystemContext();
        await CreateTenantAsync(systemContext, parentId, parentId);
        try
        {
            // The parent keeps a parent-local registry row for its child; it must not count as a
            // system database, or every nested tenant would look double registered.
            var parent = await systemContext.GetChildTenantContextAsync(parentId);
            await CreateTenantAsync(parent, childId, childId);

            var registrations = await Lookup.GetRegistrationsAsync(TestContext.Current.CancellationToken);

            Assert.False(registrations.IsSystemDatabase(parentId));
            Assert.Equal([SystemDatabaseB], registrations.GetRegisteringSystemDatabases(childId));
            Assert.Equal([SystemDatabaseB], registrations.GetRegisteringSystemDatabases(parentId));
        }
        finally
        {
            try
            {
                var parent = await systemContext.GetChildTenantContextAsync(parentId);
                using var session = await parent.GetAdminSessionAsync();
                session.StartTransaction();
                await parent.DropChildTenantAsync(session, childId);
                await session.CommitTransactionAsync();
            }
            catch (Exception)
            {
                // Cleanup only.
            }

            await ThrowawayTenant.DropAsync(systemContext, parentId);
        }
    }

    [Fact]
    public async Task UnregisteredDatabase_HasNoRegistration_AndNamesAreNormalized()
    {
        var tenantId = NewName("lookupa");
        await CreateTenantAsync(fixture.GetSystemContext(), tenantId, tenantId);
        try
        {
            Assert.Empty(await Lookup.FindRegisteringSystemDatabasesAsync(NewName("nowhere"),
                TestContext.Current.CancellationToken));

            // Mixed case and whitespace resolve to the same normalized registration.
            Assert.Equal([SystemDatabaseA],
                await Lookup.FindRegisteringSystemDatabasesAsync($"  {tenantId.ToUpperInvariant()} ",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(fixture.GetSystemContext(), tenantId);
        }
    }
}
