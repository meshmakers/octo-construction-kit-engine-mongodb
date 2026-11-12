using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     The fixed query shapes of the count parity tests (AB#8895, decision Markus + Odin 12.11.2026, AB#8793): the
///     count a totalCount-only query returns must stay exactly the count the unpaged item query returned before.
///     <see cref="CountParityTests" /> proves the old count per shape; the count-only tests compare against it.
/// </summary>
public static class CountParityCases
{
    /// <summary>One query shape on the sample runtime model.</summary>
    /// <param name="Name">Stable case name (theory data).</param>
    /// <param name="CkTypeId">The queried CK type.</param>
    /// <param name="Options">Creates the query options (a fresh instance per execution).</param>
    /// <param name="MinExpected">Lower bound of the count, so that a shape cannot pass by matching nothing.</param>
    /// <param name="Navigation">Creates the navigation pairs of a graph query (null = plain type query).</param>
    public sealed record Case(
        string Name,
        RtCkId<CkTypeId> CkTypeId,
        Func<RtEntityQueryOptions> Options,
        int MinExpected,
        Func<SampleRtModelDataFixture, ICollection<NavigationPair>>? Navigation = null);

    public static readonly IReadOnlyList<Case> All =
    [
        new("PlainType", TestCkIds.RtCkDistrictTypeId, () => RtEntityQueryOptions.Create(), 5),
        new("PlainType_IncludeArchived", TestCkIds.RtCkDistrictTypeId,
            () => RtEntityQueryOptions.Create().Global(true), 7),
        new("FieldFilter_Equals", TestCkIds.RtCkMeasuringPointTypeId,
            () => RtEntityQueryOptions.Create().FieldFilter("CounterNumber", FieldFilterOperator.Equals, "AT123"), 1),
        new("FieldFilter_Like", TestCkIds.RtCkDistrictTypeId,
            () => RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.Like, "P*"), 2),
        new("FieldFilter_InArray", TestCkIds.RtCkMeasuringPointTypeId,
            () => RtEntityQueryOptions.Create().FieldFilter("Tags", FieldFilterOperator.In, new[] { "Water" }), 1),
        new("FieldFilter_And", TestCkIds.RtCkMeasuringPointTypeId,
            () => RtEntityQueryOptions.Create()
                .FieldFilter("Name", FieldFilterOperator.Equals, "Hauptzähler")
                .FieldFilter("CounterReading", FieldFilterOperator.GreaterThan, 14565), 1),
        new("FieldFilter_Or", TestCkIds.RtCkMeasuringPointTypeId,
            () => RtEntityQueryOptions.Create(LogicalOperators.Or)
                .FieldFilter("CounterNumber", FieldFilterOperator.Equals, "AT123")
                .FieldFilter("CounterNumber", FieldFilterOperator.Equals, "AT125"), 2),
        new("AttributeSearch", TestCkIds.RtCkDistrictTypeId,
            () => RtEntityQueryOptions.Create().AttributeSearch(["Name"], "Pinzgau"), 1),
        new("TextSearch", TestCkIds.RtCkDistrictTypeId,
            () => RtEntityQueryOptions.Create().TextSearch("Pinzgau"), 0),
        new("Sorted", TestCkIds.RtCkDistrictTypeId,
            () => RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Descending), 5),
        new("Navigation_FilterMode_Existence", TestCkIds.RtCkStateOrProvinceTypeId,
            () => RtEntityQueryOptions.Create(), 1,
            f => [CreateChildrenNavigationPair(f)]),
        new("Navigation_IncludeMode", TestCkIds.RtCkStateOrProvinceTypeId,
            () => RtEntityQueryOptions.Create().UseNavigationFilterMode(NavigationFilterMode.Include), 2,
            f => [CreateChildrenNavigationPair(f)]),
        new("Navigation_CountFilter_GreaterEqualOne", TestCkIds.RtCkStateOrProvinceTypeId,
            () => RtEntityQueryOptions.Create(), 1,
            f => [CreateChildrenNavigationPair(f, new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1))]),
        new("Navigation_CountFilter_EqualsZero", TestCkIds.RtCkStateOrProvinceTypeId,
            () => RtEntityQueryOptions.Create(), 0,
            f => [CreateChildrenNavigationPair(f, new AssociationCountFilter(FieldFilterOperator.Equals, 0))]),
        new("Navigation_WithFieldFilter", TestCkIds.RtCkStateOrProvinceTypeId,
            () => RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.Like, "S*"), 1,
            f => [CreateChildrenNavigationPair(f)])
    ];

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var c in All)
        {
            data.Add(c.Name);
        }

        return data;
    }

    public static Case Get(string name) => All.Single(c => c.Name == name);

    /// <summary>The old item query of a shape: unpaged (skip/take null) or paged.</summary>
    public static async Task<long> OldTotalCountAsync(ITenantRepository repository, IOctoSession session,
        SampleRtModelDataFixture fixture, Case c, int? skip = null, int? take = null)
    {
        if (c.Navigation == null)
        {
            var result = await repository.GetRtEntitiesByTypeAsync(session, c.CkTypeId, c.Options(), skip, take);
            if (skip == null && take == null)
            {
                Assert.Equal(result.Items.Count(), result.TotalCount);
            }

            return result.TotalCount;
        }

        var graphResult = await repository.GetRtEntitiesGraphByTypeAsync(session, c.CkTypeId,
            c.Options().WithCachingDisabled(), c.Navigation(fixture), skip, take);
        if (skip == null && take == null)
        {
            Assert.Equal(graphResult.Items.Count(), graphResult.TotalCount);
        }

        return graphResult.TotalCount;
    }

    /// <summary>
    ///     ParentChild (inbound, multiplicity N) from StateOrProvince to District: Salzburg has 5 active districts,
    ///     Tirol 2 active + 2 archived (same pair as <see cref="AssociationCountFilterTests" />).
    /// </summary>
    private static NavigationPair CreateChildrenNavigationPair(SampleRtModelDataFixture fixture,
        AssociationCountFilter? countFilter = null)
    {
        var systemContext = fixture.GetSystemContext();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        var stateOrProvinceGraph =
            ckCacheService.GetRtCkType(systemContext.TenantId, TestCkIds.RtCkStateOrProvinceTypeId);
        var childrenAssociation = stateOrProvinceGraph.Associations.In.All
            .First(a => a.NavigationPropertyName == "Children");

        var districtGraph = ckCacheService.GetCkType(systemContext.TenantId, childrenAssociation.TargetCkTypeId);
        var concreteTargetType = districtGraph.GetAllDerivedTypes(true).First();

        var pair = new NavigationPair(
            [
                new PathTerm("Children", PathType.Navigation),
                new PathTerm(concreteTargetType.ToRtCkId().GetTypeName(), PathType.TargetCkTypeId)
            ],
            [],
            childrenAssociation.CkRoleId.ToRtCkId(),
            GraphDirections.Inbound,
            concreteTargetType.ToRtCkId());

        if (countFilter != null)
        {
            pair.AssociationCountFilter = countFilter;
        }

        return pair;
    }

    /// <summary>Multi-origin shape: the municipalities (inbound ParentChild) of every district.</summary>
    public static async Task<IReadOnlyList<OctoObjectId>> DistrictRtIdsAsync(ITenantRepository repository,
        IOctoSession session)
    {
        var districts = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create());
        return districts.Items.Select(d => d.RtId).ToList();
    }

    public static Task<IMultipleOriginResultSet<RtEntity>> MunicipalitiesOfAsync(ITenantRepository repository,
        IOctoSession session, IReadOnlyList<OctoObjectId> districtRtIds, int? skip = null, int? take = null)
    {
        return repository.GetRtAssociationTargetsAsync(session, districtRtIds, TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            RtEntityQueryOptions.Create(), skip, take);
    }
}
