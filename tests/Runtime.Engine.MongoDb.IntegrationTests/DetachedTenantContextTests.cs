using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#4924 — resolving a tenant through <c>ITenantLocationSource</c> instead of through the
///     installation's registry.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The shape of the proof matters more than the assertion.</b> Arming the source for a
///         tenant the registry also knows proves nothing: both routes succeed, and a regression that
///         quietly fell back to the registry would stay green. So the tenant here is one whose
///         <b>database still exists</b> and whose <b>registry row has been deleted</b> — the one
///         arrangement in which the two routes disagree. The registry route answers "does not exist";
///         the detached route answers correctly.
///     </para>
///     <para>
///         What this buys in production: an adapter-pool member never runs
///         <c>IsSystemTenantExistingAsync</c> (<c>listDatabases</c> on the <b>admin</b> connection plus
///         a system CK-model read) and never reads the tenant's registry row, so it needs neither the
///         installation's admin password nor its shared datasource password to serve a lease.
///     </para>
/// </remarks>
[Collection(SystemCollection.Name)]
public class DetachedTenantContextTests(SystemFixture fixture)
{
    private static string NewTenantId() => $"dt-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Without_a_location_source_a_tenant_the_registry_lost_cannot_be_resolved()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = NewTenantId();
        var databaseName = $"db-{tenantId}";

        await CreateTenantAsync(systemContext, tenantId, databaseName);
        var handle = await DeleteRegistryRowOnlyAsync(systemContext, tenantId);
        try
        {
            // The baseline the next test is measured against: the database is still there, the
            // registry no longer knows the tenant, and the ordinary resolve therefore fails.
            Assert.Null(await systemContext.TryFindTenantContextAsync(tenantId));
        }
        finally
        {
            await systemContext.DropTenantDatabaseAsync(handle, tenantId);
        }
    }

    [Fact]
    public async Task An_armed_location_source_resolves_a_tenant_the_registry_does_not_know()
    {
        var systemContext = fixture.GetSystemContext();
        var locationSource = fixture.GetService<TestTenantLocationSource>();
        var tenantId = NewTenantId();
        var databaseName = $"db-{tenantId}";

        await CreateTenantAsync(systemContext, tenantId, databaseName);
        var handle = await DeleteRegistryRowOnlyAsync(systemContext, tenantId);
        try
        {
            using (locationSource.Arm(tenantId, databaseName))
            {
                var context = await systemContext.TryFindTenantContextAsync(tenantId);

                Assert.NotNull(context);
                Assert.Equal(tenantId, context!.TenantId);
                Assert.Equal(databaseName, context.DatabaseName);

                // Not merely constructed — usable. A context that satisfies the assertions above and
                // cannot open a session would fail at the first Mongo command a leased pipeline issues,
                // which is exactly the kind of "green test, broken feature" this whole file exists to
                // avoid.
                var repository = context.GetTenantRepository();
                using var session = await repository.GetSessionAsync();
                Assert.NotNull(session);
            }

            // And the source really is the reason: disarmed, the same call fails again, so nothing was
            // cached along the way that would keep the tenant resolvable by itself.
            Assert.Null(await systemContext.TryFindTenantContextAsync(tenantId));
        }
        finally
        {
            await systemContext.DropTenantDatabaseAsync(handle, tenantId);
        }
    }

    /// <summary>
    ///     An armed source answers for its own tenant and no other — the property that stops a member
    ///     from being pointed at a neighbour's database.
    /// </summary>
    [Fact]
    public async Task An_armed_location_source_does_not_answer_for_another_tenant()
    {
        var systemContext = fixture.GetSystemContext();
        var locationSource = fixture.GetService<TestTenantLocationSource>();
        var tenantId = NewTenantId();
        var databaseName = $"db-{tenantId}";

        await CreateTenantAsync(systemContext, tenantId, databaseName);
        try
        {
            using (locationSource.Arm(tenantId, databaseName))
            {
                Assert.Null(await systemContext.TryFindTenantContextAsync(NewTenantId()));
            }
        }
        finally
        {
            var handle = await DeleteRegistryRowOnlyAsync(systemContext, tenantId);
            await systemContext.DropTenantDatabaseAsync(handle, tenantId);
        }
    }

    private static async Task CreateTenantAsync(ISystemContext systemContext, string tenantId,
        string databaseName)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task<TenantDeletionHandle> DeleteRegistryRowOnlyAsync(ISystemContext systemContext,
        string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var handle = await systemContext.DeleteChildTenantMetadataAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return handle;
    }
}
