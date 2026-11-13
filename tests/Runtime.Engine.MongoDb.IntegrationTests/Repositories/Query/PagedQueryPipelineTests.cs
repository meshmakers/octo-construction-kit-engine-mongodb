using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     The reads of a paged runtime-entity query (AB#8896): no <c>$facet</c>; a page pipeline that ends with
///     <c>$sort</c> → <c>$skip</c> → <c>$limit</c> and holds no enrichment lookup; the enrichment on the page only; a
///     separate count that returns no entity - and no count at all when the total is not asked for.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class PagedQueryPipelineTests(SampleRtModelDataFixture fixture)
{
    [Theory]
    [MemberData(nameof(CountParityCases.Names), MemberType = typeof(CountParityCases))]
    public async Task PagePipeline_SortsSkipsAndLimitsBeforeAnyEnrichment(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();

        IReadOnlyList<BsonDocument> stages;
        IReadOnlyList<BsonDocument> enrichmentStages;
        IReadOnlyList<BsonDocument> countStages;
        if (c.Navigation == null)
        {
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session, c.CkTypeId, c.Options());
            stages = CountOnlyParityTests.Render<RtEntity, RtEntity>(query.CreatePagePipelineStageDefinitions(1, 2));
            enrichmentStages = CountOnlyParityTests.Render<RtEntity, RtEntity>(query.GetEnrichmentStageDefinitions());
            countStages = CountOnlyParityTests.Render<RtEntity, AggregateCountResult>(
                query.CreateCountPipelineStageDefinitions());
        }
        else
        {
            var (query, _) = await repository.CreateRtEntitiesGraphByTypeQueryAsync(session, c.CkTypeId,
                c.Options(), c.Navigation(fixture));
            stages = CountOnlyParityTests.Render<RtEntityGraphItem, RtEntityGraphItem>(
                query.CreatePagePipelineStageDefinitions(1, 2));
            enrichmentStages = CountOnlyParityTests.Render<RtEntityGraphItem, RtEntityGraphItem>(
                query.GetEnrichmentStageDefinitions());
            countStages = CountOnlyParityTests.Render<RtEntityGraphItem, AggregateCountResult>(
                query.CreateCountPipelineStageDefinitions());
        }

        // Only the "Sorted" shape configures a sort order; the runtime text search filters in the $match.
        AssertPagePipeline(stages, enrichmentStages, countStages, expectSort: caseName == "Sorted");
    }

    [Fact]
    public async Task PagedQueryWithEnrichment_ReadsPageEnrichmentAndCount_NoFacet()
    {
        var c = CountParityCases.Get("Navigation_IncludeMode");
        var executed = await ExecuteGraphAsync(c, skip: 0, take: 1, includeTotalCount: true);

        Assert.Equal(new[] { "page", "enrichment", "count" }, executed.Select(e => e.Purpose));
        Assert.Equal(1, executed[0].DocumentCount);
        Assert.Equal(1, executed[1].DocumentCount);
        Assert.Equal(1, executed[2].DocumentCount);
        Assert.All(executed, e => Assert.DoesNotContain(e.Stages, s => s.GetElement(0).Name == "$facet"));
        // The enrichment runs on the ids of the page, nothing else.
        Assert.Equal("$match", executed[1].Stages[0].GetElement(0).Name);
    }

    [Theory]
    [InlineData("Navigation_IncludeMode")]
    [InlineData("Navigation_FilterMode_Existence")]
    [InlineData("Navigation_WithFieldFilter")]
    public async Task PagedQueryWithoutTotalCount_RunsNoCount(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var executed = await ExecuteGraphAsync(c, skip: 0, take: 1, includeTotalCount: false);

        Assert.DoesNotContain(executed, e => e.Purpose == "count");
        Assert.Equal("page", executed[0].Purpose);
    }

    [Fact]
    public async Task PagedTypeQueryWithoutTotalCount_RunsNoCount_AndReportsALowerBound()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var counted = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 1, 2, true);
        var uncounted = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 1, 2, false);
        var beyondTheEnd = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(), 1000, 2, false);

        await session.CommitTransactionAsync();

        Assert.True(counted.TotalCount >= 5);
        Assert.Equal(counted.Items.Select(e => e.RtId), uncounted.Items.Select(e => e.RtId));
        Assert.Equal(3, uncounted.TotalCount); // skip 1 + 2 items: a lower bound, not the count
        Assert.Empty(beyondTheEnd.Items);
        Assert.Equal(0, beyondTheEnd.TotalCount);
    }

    [Fact]
    public async Task PagedQueryWithTakeZero_ReadsNoPage()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var unpaged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create());
        var takeZero = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(), 0, 0);
        await session.CommitTransactionAsync();

        Assert.Empty(takeZero.Items);
        Assert.Equal(unpaged.TotalCount, takeZero.TotalCount);
    }

    private async Task<List<SingleOriginQuery<OctoObjectId, RtEntityGraphItem>.ExecutedPipeline>> ExecuteGraphAsync(
        CountParityCases.Case c, int skip, int take, bool includeTotalCount)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        var executed = new List<SingleOriginQuery<OctoObjectId, RtEntityGraphItem>.ExecutedPipeline>();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var (query, _) = await repository.CreateRtEntitiesGraphByTypeQueryAsync(session, c.CkTypeId,
            c.Options(), c.Navigation!(fixture));
        query.PipelineObserver = executed.Add;
        await query.ExecuteQuery(session, skip, take, includeTotalCount);
        await session.CommitTransactionAsync();

        return executed;
    }

    /// <summary>
    ///     A page pipeline: no <c>$facet</c>, no <c>$count</c>; it ends with <c>$skip</c> → <c>$limit</c>, preceded by
    ///     the only <c>$sort</c> of the pipeline when a sort order is configured and by no sort otherwise; none of the
    ///     enrichment lookups is in it, and the stages before the sort narrow the result set exactly as the count
    ///     pipeline does (the association lookups in it are the filters, not the enrichment).
    /// </summary>
    internal static void AssertPagePipeline(IReadOnlyList<BsonDocument> stages,
        IReadOnlyList<BsonDocument> enrichmentStages, IReadOnlyList<BsonDocument> countStages, bool expectSort)
    {
        var names = stages.Select(s => s.GetElement(0).Name).ToList();

        Assert.DoesNotContain("$facet", names);
        Assert.DoesNotContain("$count", names);
        Assert.Equal("$limit", names[^1]);
        Assert.Equal("$skip", names[^2]);
        foreach (var enrichmentStage in enrichmentStages)
        {
            Assert.DoesNotContain(enrichmentStage, stages);
        }

        var filterStages = stages.Take(stages.Count - 2).ToList();
        if (expectSort)
        {
            Assert.Single(names, "$sort");
            Assert.Equal("$sort", names[^3]);
            filterStages = stages.Take(stages.Count - 3).ToList();
        }
        else
        {
            Assert.DoesNotContain("$sort", names);
        }

        // Without sort and paging the page pipeline narrows exactly like the count pipeline (minus its $count).
        Assert.Equal(countStages.Take(countStages.Count - 1).ToList(), filterStages);
    }
}

/// <summary>
///     A paged query over at least 2,000 entities reads one page (AB#8896, AC 5): the page pipeline returns at most
///     <c>first</c> entity documents and the count pipeline one count document.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class PagedQueryLargeResultTests(ImportTestCkModelFixture fixture)
{
    private const int EntityCount = 2_000;

    [Fact]
    public async Task PagedQueryOver2000Entities_PagePipelineReturnsAtMostFirstDocuments()
    {
        await fixture.ClearCollectionAsync();
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        try
        {
            using (var insertSession = await repository.GetSessionAsync())
            {
                insertSession.StartTransaction();
                var continents = new List<RtEntity>();
                for (var i = 0; i < EntityCount; i++)
                {
                    var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
                    continent.RtId = OctoObjectId.GenerateNewId();
                    continent.Name = $"Continent-{i:D5}";
                    continents.Add(continent);
                }

                await repository.InsertManyRtEntityAsync(insertSession, TestCkIds.RtCkContinentTypeId, continents);
                await insertSession.CommitTransactionAsync();
            }

            var executed = new List<SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline>();
            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtContinent>(session,
                TestCkIds.RtCkContinentTypeId, RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Descending));
            query.PipelineObserver = executed.Add;
            var result = await query.ExecuteQuery(session, 20, 10);
            await session.CommitTransactionAsync();

            Assert.Equal(EntityCount, result.TotalCount);
            Assert.Equal(10, result.Items.Count());
            Assert.Equal("Continent-01979", result.Items.First().Name);

            Assert.Equal(new[] { "page", "count" }, executed.Select(e => e.Purpose));
            Assert.Equal(10, executed[0].DocumentCount);
            Assert.Equal(1, executed[1].DocumentCount);
        }
        finally
        {
            await fixture.ClearCollectionAsync();
        }
    }
}
