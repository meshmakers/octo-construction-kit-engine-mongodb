using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// AB#4829 — the lightweight registry probe that gates high-frequency event consumers. Unlike
/// TryFindTenantContextAsync it must answer purely from the registry: no tenant-context construction,
/// no resolve-time CK model imports (PosUpdateTenant fires per CK import, so a heavyweight probe would
/// double the resolve work of every setup pass).
/// </summary>
[Collection(SystemCollection.Name)]
public class SystemContextRegistryProbeTests(SystemFixture fixture)
{
    [Fact]
    public async Task IsTenantRegistered_answers_from_the_registry()
    {
        var systemContext = fixture.GetSystemContext();

        // The system tenant itself is "registered" as long as it exists.
        Assert.True(await systemContext.IsTenantRegisteredAsync(systemContext.TenantId));

        // Unknown tenants are not.
        Assert.False(await systemContext.IsTenantRegisteredAsync($"ghost-{Guid.NewGuid():N}"[..20]));

        // A real child is — including via a non-normalized id (events may carry mixed case).
        var tenantId = $"pr-{Guid.NewGuid():N}"[..20];
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, $"db-{tenantId}", tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            Assert.True(await systemContext.IsTenantRegisteredAsync(tenantId.ToUpperInvariant()));
        }
        finally
        {
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            var handle = await systemContext.DeleteChildTenantMetadataAsync(session, tenantId);
            await session.CommitTransactionAsync();
            await systemContext.DropTenantDatabaseAsync(handle, tenantId);
        }

        Assert.False(await systemContext.IsTenantRegisteredAsync(tenantId));
    }
}
