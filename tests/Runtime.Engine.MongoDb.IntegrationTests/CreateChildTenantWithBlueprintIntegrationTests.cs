using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#10060: a child tenant created with a blueprint in one call (POST tenants with blueprintId) gets the blueprint's
///     CK models installed and its seed data imported, although the tenant's registry entry is still in the caller's
///     uncommitted transaction while the blueprint is applied (AB#8796).
/// </summary>
[Collection(BlueprintServiceCollection.Name)]
public class CreateChildTenantWithBlueprintIntegrationTests(BlueprintServiceFixture fixture)
{
    private static readonly BlueprintId TestBpV1 = new("TestBp", "1.0.0");
    private static readonly RtCkId<CkTypeId> CustomerCkType = new("Test/Customer");

    [Fact]
    public async Task CreateChildTenantWithBlueprint_InstallsTheCkModelAndSeedsInOneCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"cbp-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            BlueprintApplicationResult? result;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                result = await systemContext.CreateChildTenantAsync(session, tenantId, tenantId, TestBpV1);
                await session.CommitTransactionAsync();
            }

            result.Should().NotBeNull();
            var messages = string.Join("; ",
                result!.OperationResult.Messages.Select(m => $"[{m.MessageLevel}] {m.MessageText}"));
            result.IsSuccess.Should().BeTrue(messages);
            result.EntitiesCreated.Should().Be(4, "TestBp-1.0.0 seeds two customers and two continents");

            // After the commit the tenant resolves through the registry, with the CK model of the blueprint installed.
            var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, ct);
            repository.Should().NotBeNull();
            using var readSession = await repository!.GetSessionAsync();
            var customers = await repository.GetRtEntitiesByTypeAsync(readSession, CustomerCkType,
                RtEntityQueryOptions.Create());
            customers.Items.Should().HaveCount(2);

            var current = await fixture.GetBlueprintHistory().GetCurrentAsync(tenantId, ct);
            current.Should().NotBeNull();
            current!.BlueprintId.Should().Be(TestBpV1);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task CreateChildTenantWithBlueprint_TheNewTenantDoesNotResolveOutsideTheCreatingFlow()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"cbq-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId, TestBpV1);

                // The scope ended with the call and never reached this flow: before the commit the tenant is unknown.
                (await systemContext.TryFindTenantContextAsync(tenantId)).Should().BeNull();

                await session.CommitTransactionAsync();
            }

            // After the commit the registry knows it.
            (await systemContext.TryFindTenantContextAsync(tenantId)).Should().NotBeNull();
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }
}
