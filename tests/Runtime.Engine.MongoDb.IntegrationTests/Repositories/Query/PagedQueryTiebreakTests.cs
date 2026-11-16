using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
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
///     Paging through all entities returns every entity exactly once (AB#8960): the page pipeline ends its
///     <c>$sort</c> with <c>_id</c> ascending when the caller's sort is not unique, and sorts by <c>_id</c> when the
///     caller gives no sort. Without that tiebreak <c>$skip</c>/<c>$limit</c> run over an undefined order of equal
///     sort keys, and consecutive pages repeat or miss entities.
///     <para>
///         The engine pages by skip only. The GraphQL cursor of the asset repository is an offset
///         (<c>ConnectionUtils.OffsetToCursor</c>), so cursor paging is "skip = offset of the last item + 1"; both
///         ways of walking the pages are run here.
///     </para>
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class PagedQueryTiebreakTests(ImportTestCkModelFixture fixture)
{
    private const int EntityCount = 300;
    private const int PageSize = 7;

    /// <summary>Same name everywhere: the sort key ties for every entity.</summary>
    private const string TiedName = "Continent";

    public static TheoryData<string, bool> Shapes() => new()
    {
        { "NameAscending", false },
        { "NameAscending", true },
        { "NameDescending", false },
        { "NameDescending", true },
        { "Unsorted", false },
        { "Unsorted", true }
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task PagingThroughTiedOrUnsortedEntities_ReturnsEveryEntityExactlyOnce(string shape, bool cursorPaging)
    {
        var repository = await SeedAsync();
        try
        {
            using var session = await repository.GetSessionAsync();
            var unpaged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                Options(shape));
            var all = unpaged.Items.Select(e => e.RtId).ToList();
            Assert.Equal(EntityCount, all.Count);

            var paged = new List<OctoObjectId>();
            var skip = 0;
            while (true)
            {
                var page = await repository.GetRtEntitiesByTypeAsync(session,
                    TestCkIds.RtCkContinentTypeId, Options(shape), skip, PageSize, false);
                var ids = page.Items.Select(e => e.RtId).ToList();
                if (ids.Count == 0)
                {
                    break;
                }

                paged.AddRange(ids);
                // Cursor paging continues after the offset of the last item; skip paging adds the page size.
                skip = cursorPaging ? skip + ids.Count - 1 + 1 : skip + PageSize;
            }

            Assert.Equal(paged.Count, paged.Distinct().Count());
            Assert.Equal(all.OrderBy(id => id).ToList(), paged.OrderBy(id => id).ToList());
            // The tiebreak is _id in the direction of the last sort key (ascending without a sort): within the tie
            // (here: all entities) pages follow the rtId order.
            var byRtId = shape == "NameDescending" ? all.OrderByDescending(id => id) : all.OrderBy(id => id);
            Assert.Equal(byRtId.ToList(), paged);
        }
        finally
        {
            await fixture.ClearCollectionAsync();
        }
    }

    [Theory]
    [InlineData("Unsorted", "{ \"_id\" : 1 }")]
    [InlineData("NameAscending", "{ \"attributes.name\" : 1, \"_id\" : 1 }")]
    [InlineData("NameDescending", "{ \"attributes.name\" : -1, \"_id\" : -1 }")]
    [InlineData("RtIdDescending", "{ \"_id\" : -1 }")]
    [InlineData("NameThenRtIdDescending", "{ \"attributes.name\" : 1, \"_id\" : -1 }")]
    public async Task PagePipeline_EndsItsSortWithIdUnlessTheSortIsAlreadyUnique(string shape, string expectedSort)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
            TestCkIds.RtCkContinentTypeId, Options(shape));
        var stages = CountOnlyParityTests.Render<RtEntity, RtEntity>(query.CreatePagePipelineStageDefinitions(7, 7));

        var sorts = stages.Where(s => s.GetElement(0).Name == "$sort").ToList();
        Assert.Single(sorts);
        Assert.Equal(BsonDocument.Parse(expectedSort), sorts[0]["$sort"].AsBsonDocument);
        Assert.Equal(new[] { "$sort", "$skip", "$limit" }, stages.TakeLast(3).Select(s => s.GetElement(0).Name));
    }

    /// <summary>
    ///     A near query returns the entities nearest first (<c>$geoNear</c>): an unsorted page keeps that order and gets
    ///     no <c>$sort</c>; with a caller sort the caller's order applies, with the tiebreak.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "{ \"attributes.name\" : 1, \"_id\" : 1 }")]
    public async Task PagePipeline_OfANearQuery_KeepsTheDistanceOrderWithoutACallerSort(bool sorted,
        string? expectedSort)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        var options = RtEntityQueryOptions.Create().NearGeospatialFilter("GeoPosition",
            new Point(new Position(48.3, 14.3)), null, 10_000);
        if (sorted)
        {
            options.SortOrder("Name", SortOrders.Ascending);
        }

        using var session = await repository.GetSessionAsync();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
            TestCkIds.RtCkTestPositionTypeId, options);
        var stages = CountOnlyParityTests.Render<RtEntity, RtEntity>(query.CreatePagePipelineStageDefinitions(7, 7));

        Assert.Equal("$geoNear", stages[0].GetElement(0).Name);
        var sorts = stages.Where(s => s.GetElement(0).Name == "$sort").ToList();
        if (expectedSort == null)
        {
            Assert.Empty(sorts);
        }
        else
        {
            Assert.Equal(BsonDocument.Parse(expectedSort), Assert.Single(sorts)["$sort"].AsBsonDocument);
        }
    }

    private static RtEntityQueryOptions Options(string shape) => shape switch
    {
        "Unsorted" => RtEntityQueryOptions.Create(),
        "NameAscending" => RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending),
        "NameDescending" => RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Descending),
        "RtIdDescending" => RtEntityQueryOptions.Create().SortOrder("RtId", SortOrders.Descending),
        "NameThenRtIdDescending" => RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending)
            .SortOrder("RtId", SortOrders.Descending),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    /// <summary>
    ///     Inserts the entities in a shuffled rtId order, so that neither the insertion (natural) order nor any index
    ///     order other than <c>_id</c> happens to be the rtId order.
    /// </summary>
    private async Task<ITenantRepository> SeedAsync()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var ids = Enumerable.Range(0, EntityCount).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        var random = new Random(8960);
        var shuffled = ids.OrderBy(_ => random.Next()).ToList();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var continents = new List<RtEntity>();
        foreach (var id in shuffled)
        {
            var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
            continent.RtId = id;
            continent.Name = TiedName;
            continents.Add(continent);
        }

        await repository.InsertManyRtEntityAsync(session, TestCkIds.RtCkContinentTypeId, continents);
        await session.CommitTransactionAsync();
        return repository;
    }
}
