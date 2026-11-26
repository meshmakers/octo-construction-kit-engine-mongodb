using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using MongoDB.Bson;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     The count-only form of a nested association connection (take 0 - <c>first: 0</c>, and a selection of
///     <c>totalCount</c> alone, which the asset repository maps to take 0) counts on the server: its sub-pipeline
///     loads no target document, materialises no array of targets or target ids per origin, and returns the same
///     total as the item query. Runs in <see cref="ReverseNavigationThresholdCollection" />: the dense-origin tests
///     lower the static <c>ReverseNavigationOptions.EdgeThreshold</c>, which no test of a parallel collection may see.
/// </summary>
[Collection(ReverseNavigationThresholdCollection.Name)]
public class NestedAssociationCountOnlyTests(SampleRtModelDataFixture fixture)
{
    private static readonly OctoObjectId SalzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");
    private static readonly OctoObjectId TirolRtId = OctoObjectId.Parse("68fded922b85e5d74c05a560");

    public static TheoryData<string> OptionShapes =>
        ["Plain", "Sorted", "FieldFilter", "SortedAndFieldFilter", "IncludeArchived", "IdFilter"];

    [Theory]
    [MemberData(nameof(OptionShapes))]
    public async Task CountOnlyStages_LoadNoTargetDocument_AndEndWithCount(string shape)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();

        var query = await repository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound,
            shape == "IdFilter" ? [SalzburgRtId] : null, [TestCkIds.RtCkDistrictTypeId], Options(shape));

        AssertCountOnlyAssociationStages(query.CreateCountOnlyAssociationStages());
    }

    [Theory]
    [MemberData(nameof(OptionShapes))]
    public async Task CountOnly_EqualsTheTotalOfTheItemQuery(string shape)
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        IReadOnlyList<OctoObjectId>? targetRtIds = null;
        if (shape == "IdFilter")
        {
            // Two of the districts: the id filter must narrow both paths the same way.
            var all = await DistrictsOfSalzburgAsync(repository, session, RtEntityQueryOptions.Create(), null, null);
            targetRtIds = all.Items.Take(2).Select(e => e.RtId).ToList();
        }

        var unpaged = await DistrictsOfSalzburgAsync(repository, session, Options(shape), null, null, targetRtIds);
        var paged = await DistrictsOfSalzburgAsync(repository, session, Options(shape), 0, 2, targetRtIds);
        var countOnly = await DistrictsOfSalzburgAsync(repository, session, Options(shape), null, 0, targetRtIds);
        var countOnlyWithSkip =
            await DistrictsOfSalzburgAsync(repository, session, Options(shape), 3, 0, targetRtIds);

        await session.CommitTransactionAsync();

        if (targetRtIds != null)
        {
            Assert.Equal(2, unpaged.TotalCount);
        }

        Assert.Equal(unpaged.TotalCount, unpaged.Items.Count());
        Assert.Equal(unpaged.TotalCount, paged.TotalCount);
        Assert.Equal(unpaged.TotalCount, countOnly.TotalCount);
        Assert.Equal(unpaged.TotalCount, countOnlyWithSkip.TotalCount);
        Assert.Empty(countOnly.Items);
        Assert.Empty(countOnlyWithSkip.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task ExecuteQuery_TakeZero_RunsTheCountOnlyPipeline(int? skip)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var observed = new List<IReadOnlyList<BsonDocument>>();
        var query = await repository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, null,
            [TestCkIds.RtCkDistrictTypeId], Options("Sorted"));
        query.CountOnlyPipelineObserver = observed.Add;

        var result = await query.ExecuteQuery(session, skip, 0);

        await session.CommitTransactionAsync();

        var stages = Assert.Single(observed);
        AssertCountOnlyAssociationStages(stages);
        Assert.Empty(Assert.Single(result).Value.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task ExecuteQuery_OtherTakes_DoNotRunTheCountOnlyPipeline(int? take)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var observed = new List<IReadOnlyList<BsonDocument>>();
        var query = await repository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, null,
            [TestCkIds.RtCkDistrictTypeId], Options("Sorted"));
        query.CountOnlyPipelineObserver = observed.Add;

        var result = await query.ExecuteQuery(session, take.HasValue ? 0 : null, take);

        await session.CommitTransactionAsync();

        Assert.Empty(observed);
        Assert.NotEmpty(Assert.Single(result).Value.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task ExecuteQuery_TakeZero_WithAGeospatialFilter_KeepsTheItemPath(int? skip)
    {
        // The count-only pipeline has no $geoNear stage, so take 0 with a geospatial filter runs the item path for
        // one item and drops the targets: its count is always the total the item query reports for the same filter.
        // (Test/TestPosition is the only test type with a geo attribute; Salzburg has no edge to one, so both totals
        // are 0. That the item path itself ignores the filter is a known bug.)
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var options = RtEntityQueryOptions.Create().NearGeospatialFilter("GeoPosition",
            new Point(new Position(47.8, 13.0)), null, 10_000);
        var observed = new List<IReadOnlyList<BsonDocument>>();
        var query = await repository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, null,
            [TestCkIds.RtCkTestPositionTypeId], options);
        query.CountOnlyPipelineObserver = observed.Add;

        var countOnly = Assert.Single(await query.ExecuteQuery(session, skip, 0)).Value;
        var unpaged = Assert.Single(await query.ExecuteQuery(session, null, null)).Value;

        await session.CommitTransactionAsync();

        Assert.Empty(observed);
        Assert.Equal(unpaged.TotalCount, countOnly.TotalCount);
        Assert.Empty(countOnly.Items);
    }

    [Fact]
    public async Task CountOnly_OriginWithoutMatchingTargets_CountsZero()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var countOnly = await DistrictsOfSalzburgAsync(repository, session,
            RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.Equals, "no-such-district"),
            null, 0);

        await session.CommitTransactionAsync();

        Assert.Equal(0, countOnly.TotalCount);
        Assert.Empty(countOnly.Items);
    }

    [Fact]
    public async Task CountOnly_AnyDirection_SumsBothDirections()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var unpaged = await repository.GetRtAssociationTargetsAsync(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
            GraphDirections.Any, null, RtEntityQueryOptions.Create());
        var countOnly = await repository.GetRtAssociationTargetsAsync(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
            GraphDirections.Any, null, RtEntityQueryOptions.Create(), null, 0);

        await session.CommitTransactionAsync();

        Assert.Equal(unpaged.Single().Value.TotalCount, countOnly.Single().Value.TotalCount);
        Assert.Empty(countOnly.Single().Value.Items);
    }

    [Fact]
    public async Task CountOnly_ExcludesArchivedTargets_AndIncludesThemWhenAsked()
    {
        // Tirol has four district edges; two of the districts are archived.
        var repository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var countOnly = await DistrictsOfTirolAsync(repository, session, RtEntityQueryOptions.Create(), 0);
        var unpagedGlobal = await DistrictsOfTirolAsync(repository, session, RtEntityQueryOptions.Create().Global(true),
            null);
        var countOnlyGlobal = await DistrictsOfTirolAsync(repository, session, RtEntityQueryOptions.Create().Global(true),
            0);

        await session.CommitTransactionAsync();

        Assert.Equal(2, countOnly.TotalCount);
        Assert.Equal(4, unpagedGlobal.TotalCount);
        Assert.Equal(4, countOnlyGlobal.TotalCount);
        Assert.Empty(countOnly.Items);
        Assert.Empty(countOnlyGlobal.Items);
    }

    [Theory]
    [InlineData(false, false, 4)]
    [InlineData(true, false, 4)]
    [InlineData(false, true, 6)]
    public async Task CountOnly_OfADenseOriginWithoutTargetFilter_IsTheIndexEdgeCount_WithoutTargetProbes(bool sorted,
        bool salzburg, int expectedEdgeCount)
    {
        // A dense origin (more edges than the threshold) without a target filter is counted on the edge index
        // alone: no per-edge target probe runs, and the count is the number of edges - for Tirol four,
        // although two of its districts are archived. Salzburg has six edges, well above threshold + 1: the count
        // is the full edge count, not the capped routing probe (4).
        var originRtId = salzburg ? SalzburgRtId : TirolRtId;
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var originalThreshold = ReverseNavigationOptions.EdgeThreshold;
        ReverseNavigationOptions.EdgeThreshold = 3;
        try
        {
            var observed = new List<IReadOnlyList<BsonDocument>>();
            var query = await repository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [originRtId],
                TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound,
                null, [TestCkIds.RtCkDistrictTypeId], sorted ? Options("Sorted") : Options("Plain"));
            query.CountOnlyPipelineObserver = observed.Add;

            var result = await query.ExecuteQuery(session, null, 0);

            await session.CommitTransactionAsync();

            Assert.Empty(observed);
            var origin = Assert.Single(result);
            Assert.Equal(originRtId, origin.Key.RtId);
            Assert.Equal(expectedEdgeCount, origin.Value.TotalCount);
            Assert.Empty(origin.Value.Items);
        }
        finally
        {
            ReverseNavigationOptions.EdgeThreshold = originalThreshold;
        }
    }

    [Fact]
    public async Task CountOnly_OfADenseOriginWithAFieldFilter_StaysExact()
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var originalThreshold = ReverseNavigationOptions.EdgeThreshold;
        ReverseNavigationOptions.EdgeThreshold = 0;
        try
        {
            var observed = new List<IReadOnlyList<BsonDocument>>();
            var query = await repository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [TirolRtId],
                TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound,
                null, [TestCkIds.RtCkDistrictTypeId],
                RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.NotEquals, "no-such-district"));
            query.CountOnlyPipelineObserver = observed.Add;

            var result = await query.ExecuteQuery(session, null, 0);

            await session.CommitTransactionAsync();

            Assert.Single(observed);
            Assert.Equal(2, Assert.Single(result).Value.TotalCount);
        }
        finally
        {
            ReverseNavigationOptions.EdgeThreshold = originalThreshold;
        }
    }

    [Fact]
    public async Task CountOnly_OfABatchOfDenseAndSparseOrigins_AnswersBoth()
    {
        // Salzburg has six district edges (dense at threshold 5, edge count), Tirol four (sparse, exact count 2).
        var repository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var originalThreshold = ReverseNavigationOptions.EdgeThreshold;
        ReverseNavigationOptions.EdgeThreshold = 5;
        try
        {
            var result = await repository.GetRtAssociationTargetsAsync(session, [SalzburgRtId, TirolRtId],
                TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create(), null, 0);

            await session.CommitTransactionAsync();

            Assert.Equal(2, result.Count);
            Assert.Equal(6, result.Single(r => r.Key.RtId == SalzburgRtId).Value.TotalCount);
            Assert.Equal(2, result.Single(r => r.Key.RtId == TirolRtId).Value.TotalCount);
            Assert.All(result, r => Assert.Empty(r.Value.Items));
        }
        finally
        {
            ReverseNavigationOptions.EdgeThreshold = originalThreshold;
        }
    }

    /// <summary>
    ///     The sub-pipeline over the association collection of the count-only path: a <c>$match</c> on the edges, one
    ///     index-backed target lookup per collection (<c>localField</c> → <c>_id</c>) whose own pipeline only filters
    ///     and then keeps nothing but <c>_id</c>, and a single <c>$count</c> at the end - so the origin receives one
    ///     count document, never an array of targets or target ids. No sort, paging, window or grouping stage.
    /// </summary>
    internal static void AssertCountOnlyAssociationStages(IReadOnlyList<BsonDocument> stages)
    {
        Assert.Equal("$match", stages[0].GetElement(0).Name);
        Assert.Equal("$count", stages[^1].GetElement(0).Name);
        Assert.Single(stages, s => s.GetElement(0).Name == "$count");

        var forbidden = new[] { "$facet", "$skip", "$limit", "$sort", "$setWindowFields", "$group" };
        Assert.All(stages, s => Assert.DoesNotContain(s.GetElement(0).Name, forbidden));

        var lookups = stages.Where(s => s.GetElement(0).Name == "$lookup").Select(s => s["$lookup"].AsBsonDocument)
            .ToList();
        Assert.NotEmpty(lookups);
        foreach (var lookup in lookups)
        {
            Assert.Equal("_id", lookup["foreignField"].AsString);
            Assert.True(lookup.Contains("localField"));

            var targetPipeline = lookup["pipeline"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();
            Assert.Equal(new BsonDocument("$project", new BsonDocument("_id", 1)), targetPipeline[^1]);
            Assert.All(targetPipeline.Take(targetPipeline.Count - 1),
                s => Assert.Equal("$match", s.GetElement(0).Name));
        }
    }

    private static RtEntityQueryOptions Options(string shape) => shape switch
    {
        "Plain" or "IdFilter" => RtEntityQueryOptions.Create(),
        "Sorted" => RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Descending),
        "FieldFilter" => RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.Like, "*a*"),
        "SortedAndFieldFilter" => RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending)
            .FieldFilter("Name", FieldFilterOperator.Like, "*a*"),
        "IncludeArchived" => RtEntityQueryOptions.Create().Global(true),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    private static async Task<IResultSet<RtEntity>> DistrictsOfSalzburgAsync(
        Contracts.MongoDb.Repositories.ITenantRepository repository, IOctoSession session,
        RtEntityQueryOptions options, int? skip, int? take, IReadOnlyList<OctoObjectId>? targetRtIds = null)
    {
        var result = await repository.GetRtAssociationTargetsAsync(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
            GraphDirections.Inbound, targetRtIds, options, skip, take);
        return result.Single().Value;
    }

    private static async Task<IResultSet<RtEntity>> DistrictsOfTirolAsync(
        Contracts.MongoDb.Repositories.ITenantRepository repository, IOctoSession session,
        RtEntityQueryOptions options, int? take)
    {
        var result = await repository.GetRtAssociationTargetsAsync(session, [TirolRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
            GraphDirections.Inbound, null, options, null, take);
        return result.Single().Value;
    }
}
