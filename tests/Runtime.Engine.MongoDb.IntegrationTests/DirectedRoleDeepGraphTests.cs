using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
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
///     AB#5003: directed role-set deep graph. Each follow rule walks one association role in one
///     direction, so a hub is a dead-end and the closure does not over-collect. Mirrors the identity
///     exchange, where a permission is collected together with the policies and roles pointing INTO
///     it, but a collected role does not drag in its other permissions.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class DirectedRoleDeepGraphTests
{
    private static readonly RtCkId<CkAssociationRoleId> ReferencesRoleId = new("Test/References");
    private static readonly RtCkId<CkAssociationRoleId> ParentChildRoleId =
        SystemCkIds.RtCkParentChildRoleId;

    private readonly SampleRtModelDataFixture _fixture;

    public DirectedRoleDeepGraphTests(SampleRtModelDataFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    /// <summary>
    ///     Directed chain A &#8594;References&#8594; B &#8594;References&#8594; C, plus a side edge B &#8594;References&#8594; S.
    ///     Starting at C and following References INBOUND (target&#8594;origin) collects B and A, but NOT
    ///     S: S is only reachable from B in the OUTBOUND direction, which the inbound rule never walks.
    ///     That is the anti-over-collect guarantee directed following provides.
    /// </summary>
    [Fact]
    public async Task Inbound_DoesNotFollowOutboundSideEdges()
    {
        var (repository, a, b, c, s) = await SeedDirectedChainAsync();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtDeepGraphAsync(session, [c], TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create(),
            [new RtDeepGraphFollowSpec(ReferencesRoleId, GraphDirections.Inbound)]);
        await session.CommitTransactionAsync();

        var ids = result.Items.Select(i => i.Id.RtId).ToList();
        Assert.Equal(3, result.TotalCount);
        Assert.Contains(c, ids);
        Assert.Contains(b, ids);
        Assert.Contains(a, ids);
        Assert.DoesNotContain(s, ids);
    }

    /// <summary>
    ///     The same chain followed OUTBOUND from A collects B, C AND the side node S — the mirror of
    ///     the inbound case, confirming direction actually gates the walk.
    /// </summary>
    [Fact]
    public async Task Outbound_FollowsForwardEdgesIncludingSideEdge()
    {
        var (repository, a, b, c, s) = await SeedDirectedChainAsync();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtDeepGraphAsync(session, [a], TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create(),
            [new RtDeepGraphFollowSpec(ReferencesRoleId, GraphDirections.Outbound)]);
        await session.CommitTransactionAsync();

        var ids = result.Items.Select(i => i.Id.RtId).ToList();
        Assert.Equal(4, result.TotalCount);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
        Assert.Contains(c, ids);
        Assert.Contains(s, ids);

        // A's collected row carries its outbound References edge to B (edges of any role between
        // collected nodes travel with the graph, via the shared tail).
        var aRow = result.Items.Single(i => i.Id.RtId == a);
        Assert.Contains(aRow.Associations,
            association => association.AssociationRoleId == ReferencesRoleId && association.TargetRtId == b);
    }

    /// <summary>
    ///     A ParentChild hierarchy Continent &#8592; Country &#8592; StateOrProvince is resolved transitively in the
    ///     single server-side $graphLookup of one inbound ParentChild rule — the app loop does not
    ///     walk per level.
    /// </summary>
    [Fact]
    public async Task Inbound_ResolvesHierarchyTransitivelyInOneRule()
    {
        var (repository, continent, country, subCountry) = await SeedHierarchyAsync();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtDeepGraphAsync(session, [continent], TestCkIds.RtCkContinentTypeId,
            RtEntityQueryOptions.Create(),
            [new RtDeepGraphFollowSpec(ParentChildRoleId, GraphDirections.Inbound)]);
        await session.CommitTransactionAsync();

        var ids = result.Items.Select(i => i.Id.RtId).ToList();
        Assert.Contains(continent, ids);
        Assert.Contains(country, ids);
        Assert.Contains(subCountry, ids);
    }

    [Fact]
    public async Task NullSpecs_FallBackToParentChild()
    {
        var (repository, continent, country, subCountry) = await SeedHierarchyAsync();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtDeepGraphAsync(session, [continent], TestCkIds.RtCkContinentTypeId,
            RtEntityQueryOptions.Create(), followSpecs: null);
        await session.CommitTransactionAsync();

        var ids = result.Items.Select(i => i.Id.RtId).ToList();
        Assert.Contains(continent, ids);
        Assert.Contains(country, ids);
        Assert.Contains(subCountry, ids);
    }

    private async Task<(ITenantRepository Repository, OctoObjectId A, OctoObjectId B, OctoObjectId C,
            OctoObjectId S)>
        SeedDirectedChainAsync()
    {
        await _fixture.ClearCollectionAsync();
        var repository = _fixture.GetSystemContext().GetTenantRepository();

        var a = await InsertCustomerAsync(repository, "A");
        var b = await InsertCustomerAsync(repository, "B");
        var c = await InsertCustomerAsync(repository, "C");
        var s = await InsertCustomerAsync(repository, "S");

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, new List<IEntityUpdateInfo<RtEntity>>(),
        [
            Edge(a, b), // A -> B
            Edge(b, c), // B -> C
            Edge(b, s)  // B -> S  (side edge, only reachable outbound from B)
        ], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);

        return (repository, a, b, c, s);

        AssociationUpdateInfo Edge(OctoObjectId origin, OctoObjectId target) =>
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkCustomerTypeId, origin),
                new RtEntityId(TestCkIds.RtCkCustomerTypeId, target),
                ReferencesRoleId);
    }

    private async Task<(ITenantRepository Repository, OctoObjectId Continent, OctoObjectId Country,
            OctoObjectId SubCountry)>
        SeedHierarchyAsync()
    {
        await _fixture.ClearCollectionAsync();
        var repository = _fixture.GetSystemContext().GetTenantRepository();

        var continent = await InsertContinentAsync(repository, "Continent");
        var country = await InsertCountryAsync(repository, "C1", continent);
        var subCountry = await InsertStateOrProvinceAsync(repository, "SP1", country);
        return (repository, continent, country, subCountry);
    }

    private static async Task<OctoObjectId> InsertCustomerAsync(ITenantRepository repository, string name)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var customer = await repository.CreateTransientRtEntityAsync<RtCustomer>();
        customer.RtId = OctoObjectId.GenerateNewId();
        customer.Name = new RtContactNameRecord { LastName = name };
        await repository.InsertOneRtEntityAsync(session, customer);
        await session.CommitTransactionAsync();
        return customer.RtId;
    }

    private static async Task<OctoObjectId> InsertContinentAsync(ITenantRepository repository, string name)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = name;
        await repository.InsertOneRtEntityAsync(session, continent);
        await session.CommitTransactionAsync();
        return continent.RtId;
    }

    private static async Task<OctoObjectId> InsertCountryAsync(ITenantRepository repository, string isoCode,
        OctoObjectId parentContinentId)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = $"Country-{isoCode}";
        country.ISOCode = isoCode;
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(country) },
        [
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkCountryTypeId, country.RtId),
                new RtEntityId(TestCkIds.RtCkContinentTypeId, parentContinentId),
                ParentChildRoleId)
        ], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
        return country.RtId;
    }

    private static async Task<OctoObjectId> InsertStateOrProvinceAsync(ITenantRepository repository, string name,
        OctoObjectId parentCountryId)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var stateOrProvince = await repository.CreateTransientRtEntityAsync<RtStateOrProvince>();
        stateOrProvince.RtId = OctoObjectId.GenerateNewId();
        stateOrProvince.Name = name;
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(stateOrProvince) },
        [
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, stateOrProvince.RtId),
                new RtEntityId(TestCkIds.RtCkCountryTypeId, parentCountryId),
                ParentChildRoleId)
        ], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
        return stateOrProvince.RtId;
    }
}
