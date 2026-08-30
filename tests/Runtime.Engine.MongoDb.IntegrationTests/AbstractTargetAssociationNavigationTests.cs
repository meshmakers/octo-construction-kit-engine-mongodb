using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5000: association navigation whose target is an abstract type ABOVE the collection-root
///     level (here the Customer "References" association targeting System/Entity, mirroring
///     System/Related). Such a target has no defining collection root — the entity lookups must fan
///     out over the collection roots of the concrete derived types instead of throwing
///     "has no defining collection root". Uses the data-permission fixture so the multi-root
///     visibility join (AB#4986) can be exercised too.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class AbstractTargetAssociationNavigationTests(DataPermissionTestFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SystemEntityTypeId = new("System/Entity");

    private static readonly RtSecurityContext Employee1 =
        RtSecurityContext.ForUser("user-1", ["TestEmployee"]);

    private static readonly RtSecurityContext Employee2 =
        RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    [Fact]
    public async Task AbstractTarget_CountFilter_SpansCollectionRoots()
    {
        var (repository, _, _, _) = await SeedAsync();

        // Both referenced entities count, although they live in two different collections
        // (Customer root and Location root).
        Assert.Single((await QueryCustomersWithReferenceCountAsync(repository, null,
            FieldFilterOperator.GreaterEqualThan, 2)).Items);
        Assert.Empty((await QueryCustomersWithReferenceCountAsync(repository, null,
            FieldFilterOperator.GreaterEqualThan, 3)).Items);
    }

    [Fact]
    public async Task AbstractTarget_EnrichmentCells_CarryAssociationsFromAllRoots()
    {
        var (repository, origin, _, _) = await SeedAsync();

        var result = await QueryCustomersWithReferenceCountAsync(repository, null,
            FieldFilterOperator.GreaterEqualThan, 1);
        var item = Assert.Single(result.Items);
        Assert.Equal(origin, item.RtId);
        Assert.Equal(2, item.Associations.Count(a => a.NavigationPropertyName == "References"));
    }

    [Fact]
    public async Task AbstractTarget_SecurityFilter_CountsOnlyVisibleRoots()
    {
        var (repository, _, _, continentTarget) = await SeedAsync();

        // Protect Continent owned-only: employee1 loses sight of employee2's continent, so only
        // the (unprotected) customer target remains countable.
        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.continents",
                new HashSet<string> { TestCkIds.RtCkContinentTypeId.SemanticVersionedFullName },
                [RtDataAction.Read], OwnedOnly: true, AuditOnly: false,
                new HashSet<string> { "TestEmployee" })
        ]);
        try
        {
            Assert.Empty((await QueryCustomersWithReferenceCountAsync(repository, Employee1,
                FieldFilterOperator.GreaterEqualThan, 2)).Items);
            Assert.Single((await QueryCustomersWithReferenceCountAsync(repository, Employee1,
                FieldFilterOperator.GreaterEqualThan, 1)).Items);

            // The owner of the continent still counts both ends.
            Assert.Single((await QueryCustomersWithReferenceCountAsync(repository, Employee2,
                FieldFilterOperator.GreaterEqualThan, 2)).Items);
            _ = continentTarget;
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private async Task<(ITenantRepository Repository, OctoObjectId Origin, OctoObjectId CustomerTarget,
            OctoObjectId ContinentTarget)>
        SeedAsync()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        // Origin customer plus two referenced entities in two different collection roots
        // (Customer root and Location root). The customer target belongs to employee1, the
        // continent to employee2 (for the visibility test). References edges are created by
        // employee1.
        var origin = await InsertCustomerAsync(repository, Employee1, "Origin");
        var customerTarget = await InsertCustomerAsync(repository, Employee1, "Referenced-Customer");
        var continentTarget = await InsertContinentAsync(repository, Employee2, "Referenced-Continent");

        await InsertReferencesAsync(repository, Employee1, origin,
        [
            new RtEntityId(TestCkIds.RtCkCustomerTypeId, customerTarget),
            new RtEntityId(TestCkIds.RtCkContinentTypeId, continentTarget)
        ]);

        return (repository, origin, customerTarget, continentTarget);
    }

    private static async Task<OctoObjectId> InsertCustomerAsync(ITenantRepository repository,
        RtSecurityContext securityContext, string name)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var customer = await repository.CreateTransientRtEntityAsync<RtCustomer>();
        customer.RtId = OctoObjectId.GenerateNewId();
        customer.Name = new RtContactNameRecord { LastName = name };
        await repository.InsertOneRtEntityAsync(session, customer);
        await session.CommitTransactionAsync();
        return customer.RtId;
    }

    private static async Task<OctoObjectId> InsertContinentAsync(ITenantRepository repository,
        RtSecurityContext securityContext, string name)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = name;
        await repository.InsertOneRtEntityAsync(session, continent);
        await session.CommitTransactionAsync();
        return continent.RtId;
    }

    private static async Task InsertReferencesAsync(ITenantRepository repository,
        RtSecurityContext securityContext, OctoObjectId originCustomerId, IReadOnlyList<RtEntityId> targets)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, new List<IEntityUpdateInfo<RtEntity>>(),
            targets.Select(t => AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkCustomerTypeId, originCustomerId), t,
                new RtCkId<CkAssociationRoleId>("Test/References"))).ToList(),
            operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
    }

    private async Task<IResultSet<RtEntityGraphItem>> QueryCustomersWithReferenceCountAsync(
        ITenantRepository repository, RtSecurityContext? securityContext,
        FieldFilterOperator countOperator, int comparisonValue)
    {
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var tenantId = fixture.GetSystemContext().TenantId;

        var customerGraph = ckCacheService.GetRtCkType(tenantId, TestCkIds.RtCkCustomerTypeId);
        var referencesAssociation = customerGraph.Associations.Out.All
            .First(a => a.NavigationPropertyName == "References");

        var pair = new NavigationPair(
            [
                new PathTerm("References", PathType.Navigation),
                new PathTerm(SystemEntityTypeId.GetTypeName(), PathType.TargetCkTypeId)
            ],
            [],
            referencesAssociation.CkRoleId.ToRtCkId(),
            GraphDirections.Outbound,
            SystemEntityTypeId)
        {
            AssociationCountFilter = new AssociationCountFilter(countOperator, comparisonValue)
        };

        using var session = securityContext == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var result = await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create(), [pair]);
        await session.CommitTransactionAsync();
        return result;
    }
}
