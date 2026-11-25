using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     The count-only form of a nested association connection (take 0 - <c>first: 0</c>, and a selection of
///     <c>totalCount</c> alone, which the asset repository maps to take 0) counts on the server: its sub-pipeline
///     loads no target document, materialises no array of targets or target ids per origin, and returns the same
///     total as the item query.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class NestedAssociationCountOnlyTests(SampleRtModelDataFixture fixture)
{
    private static readonly OctoObjectId SalzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");

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

        var unpaged = await DistrictsOfSalzburgAsync(repository, session, Options(shape), null, null);
        var paged = await DistrictsOfSalzburgAsync(repository, session, Options(shape), 0, 2);
        var countOnly = await DistrictsOfSalzburgAsync(repository, session, Options(shape), null, 0);
        var countOnlyWithSkip = await DistrictsOfSalzburgAsync(repository, session, Options(shape), 3, 0);

        await session.CommitTransactionAsync();

        Assert.Equal(unpaged.TotalCount, unpaged.Items.Count());
        Assert.Equal(unpaged.TotalCount, paged.TotalCount);
        Assert.Equal(unpaged.TotalCount, countOnly.TotalCount);
        Assert.Equal(unpaged.TotalCount, countOnlyWithSkip.TotalCount);
        Assert.Empty(countOnly.Items);
        Assert.Empty(countOnlyWithSkip.Items);
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
        RtEntityQueryOptions options, int? skip, int? take)
    {
        var result = await repository.GetRtAssociationTargetsAsync(session, [SalzburgRtId],
            TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
            GraphDirections.Inbound, null, options, skip, take);
        return result.Single().Value;
    }
}
