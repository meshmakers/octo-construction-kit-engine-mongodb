using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The reproduction of AB#8797 on two real instances sharing one MongoDB server: with a tenant-database prefix
///     per instance both create the same tenant id with the default database name; without one the second is refused.
/// </summary>
/// <remarks>
///     Runs in the two-instance collection (serial with its other tests) and sets the prefixes back in <c>finally</c>.
/// </remarks>
[Collection(TwoInstanceCollection.Name)]
public class TwoInstanceTenantDatabasePrefixTests(TwoInstanceFixture fixture)
{
    private ISystemContext InstanceA => fixture.GetSystemContext();

    private ISystemContext InstanceB => fixture.Other.GetSystemContext();

    private OctoSystemConfiguration ConfigurationA => fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

    private OctoSystemConfiguration ConfigurationB =>
        fixture.Other.GetService<IOptions<OctoSystemConfiguration>>().Value;

    private static string NewId(string stem) => $"{stem}{Guid.NewGuid():N}"[..16].ToLowerInvariant();

    private static async Task CreateDefaultTenantAsync(ISystemContext instance, OctoSystemConfiguration configuration,
        string tenantId)
    {
        using var session = await instance.GetAdminSessionAsync();
        session.StartTransaction();
        await instance.CreateChildTenantAsync(session,
            TenantDatabaseNames.DeriveDefault(configuration.TenantDatabasePrefix, tenantId), tenantId);
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task WithDifferentPrefixes_BothInstancesCreateTheSameTenantId_WithTheDefaultName()
    {
        var tenantId = NewId("meshtest");
        var previousA = ConfigurationA.TenantDatabasePrefix;
        var previousB = ConfigurationB.TenantDatabasePrefix;
        ConfigurationA.TenantDatabasePrefix = "pfa_";
        ConfigurationB.TenantDatabasePrefix = "pfb_";
        try
        {
            await CreateDefaultTenantAsync(InstanceA, ConfigurationA, tenantId);
            await CreateDefaultTenantAsync(InstanceB, ConfigurationB, tenantId);

            Assert.Equal("pfa_" + tenantId, (await InstanceA.TryFindTenantContextAsync(tenantId))!.DatabaseName);
            Assert.Equal("pfb_" + tenantId, (await InstanceB.TryFindTenantContextAsync(tenantId))!.DatabaseName);
        }
        finally
        {
            try
            {
                await ThrowawayTenant.DropAsync(InstanceA, tenantId);
                await ThrowawayTenant.DropAsync(InstanceB, tenantId);
            }
            finally
            {
                // The fixture is shared: the prefixes go back even when a cleanup fails.
                ConfigurationA.TenantDatabasePrefix = previousA;
                ConfigurationB.TenantDatabasePrefix = previousB;
            }
        }
    }

    [Fact]
    public async Task WithoutPrefixes_TheSecondInstanceIsRefused_AsBefore()
    {
        var tenantId = NewId("meshtest");
        Assert.Equal(string.Empty, ConfigurationA.TenantDatabasePrefix);
        Assert.Equal(string.Empty, ConfigurationB.TenantDatabasePrefix);
        try
        {
            await CreateDefaultTenantAsync(InstanceA, ConfigurationA, tenantId);

            var exception = await Assert.ThrowsAsync<TenantException>(() =>
                CreateDefaultTenantAsync(InstanceB, ConfigurationB, tenantId));

            Assert.True(exception.IsConflict);
            Assert.Null(await InstanceB.TryFindTenantContextAsync(tenantId));
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(tenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantId);
            await ThrowawayTenant.DropAsync(InstanceA, tenantId);
        }
    }
}
