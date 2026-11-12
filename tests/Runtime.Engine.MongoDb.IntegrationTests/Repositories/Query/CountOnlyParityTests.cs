using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     Count parity, part 2 (AB#8895): the count-only path (<see cref="ITenantRepository.CountRtEntitiesByTypeAsync" />,
///     <see cref="ITenantRepository.CountRtEntitiesGraphByTypeAsync" />) returns exactly the count of the old unpaged
///     item query for every shape of <see cref="CountParityCases" />, and its pipeline returns no runtime entity.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class CountOnlyParityTests(SampleRtModelDataFixture fixture)
{
    [Theory]
    [MemberData(nameof(CountParityCases.Names), MemberType = typeof(CountParityCases))]
    public async Task CountOnly_EqualsTheOldItemQueryCount(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var old = await CountParityCases.OldTotalCountAsync(repository, session, fixture, c);
        var count = c.Navigation == null
            ? await repository.CountRtEntitiesByTypeAsync(session, c.CkTypeId, c.Options())
            : await repository.CountRtEntitiesGraphByTypeAsync(session, c.CkTypeId, c.Options(),
                c.Navigation(fixture));

        await session.CommitTransactionAsync();

        Assert.Equal(old, count);
    }

    [Theory]
    [MemberData(nameof(CountParityCases.Names), MemberType = typeof(CountParityCases))]
    public async Task CountOnly_PipelineReturnsNoDocument(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();

        IReadOnlyList<BsonDocument> stages;
        IReadOnlyList<BsonDocument> enrichmentStages;
        if (c.Navigation == null)
        {
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session, c.CkTypeId, c.Options());
            stages = Render<RtEntity, AggregateCountResult>(query.CreateCountPipelineStageDefinitions());
            enrichmentStages = Render<RtEntity, RtEntity>(query.GetEnrichmentStageDefinitions());
        }
        else
        {
            var (query, _) = await repository.CreateRtEntitiesGraphByTypeQueryAsync(session, c.CkTypeId,
                c.Options(), c.Navigation(fixture));
            stages = Render<RtEntityGraphItem, AggregateCountResult>(query.CreateCountPipelineStageDefinitions());
            enrichmentStages = Render<RtEntityGraphItem, RtEntityGraphItem>(query.GetEnrichmentStageDefinitions());
        }

        AssertCountOnlyPipeline(stages, enrichmentStages);
    }

    [Fact]
    public async Task CountOnly_WithFieldFilterAndNavigationFilter_KeepsTheFilterStagesBeforeCount()
    {
        var c = CountParityCases.Get("Navigation_CountFilter_GreaterEqualOne");
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        var (query, _) = await repository.CreateRtEntitiesGraphByTypeQueryAsync(session, c.CkTypeId,
            c.Options(), c.Navigation!(fixture));
        var stages = Render<RtEntityGraphItem, AggregateCountResult>(query.CreateCountPipelineStageDefinitions());

        Assert.Equal("$match", stages[0].GetElement(0).Name);
        // The association count filter needs its lookup before the count, else the count would ignore it.
        Assert.Contains(stages.Take(stages.Count - 1), s => s.GetElement(0).Name == "$lookup");
        Assert.Equal("$count", stages[^1].GetElement(0).Name);
    }

    [Fact]
    public async Task CountOnly_AssociationCountFilters_SplitTheTypeExactly()
    {
        // The parity cases accept any count for "= 0"; here the two count filters must partition the type, which
        // fails when either path ignored the filter (both would then count every state).
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var zero = CountParityCases.Get("Navigation_CountFilter_EqualsZero");
        var atLeastOne = CountParityCases.Get("Navigation_CountFilter_GreaterEqualOne");

        using var session = await repository.GetSessionAsync();
        var all = await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());
        var withoutChildren = await repository.CountRtEntitiesGraphByTypeAsync(session, zero.CkTypeId,
            zero.Options(), zero.Navigation!(fixture));
        var withChildren = await repository.CountRtEntitiesGraphByTypeAsync(session, atLeastOne.CkTypeId,
            atLeastOne.Options(), atLeastOne.Navigation!(fixture));

        Assert.True(withChildren >= 1);
        Assert.Equal(all, withoutChildren + withChildren);
    }

    [Fact]
    public async Task CountOnly_FieldFilterOnANavigatedAttribute_EqualsTheOldCountAndNarrowsIt()
    {
        // A field filter on the navigation target is merged into the navigation pair
        // (RtPathEvaluator.MergeFieldFilterToNavigationPairs) and must narrow the count like the item query.
        var systemContext = fixture.GetSystemContext();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var repository = systemContext.GetTenantRepository();
        const string parentName = "parent.testStateOrProvince->name";

        ICollection<NavigationPair> Pairs() => RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService,
            systemContext.TenantId, TestCkIds.CkDistrictTypeId, ["name", parentName]);
        RtEntityQueryOptions Filtered() =>
            RtEntityQueryOptions.Create().FieldFilter(parentName, FieldFilterOperator.Equals, "Salzburg");

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var old = await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            Filtered().WithCachingDisabled(), Pairs());
        var count = await repository.CountRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            Filtered(), Pairs());
        var unfiltered = await repository.CountRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(), Pairs());

        await session.CommitTransactionAsync();

        Assert.Equal(old.Items.Count(), old.TotalCount);
        Assert.Equal(old.TotalCount, count);
        Assert.True(count >= 1, $"no district of Salzburg counted ({count})");
        Assert.True(count < unfiltered, $"the navigated filter did not narrow the count ({count} of {unfiltered})");
    }

    [Fact]
    public async Task CountOnly_NothingMatches_ReturnsZero()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        var count = await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.Equals, "no-such-district-8895"));

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountOnly_MultiOrigin_EqualsTheUnpagedCountPerOrigin()
    {
        // Association targets of several origins already have a count-only form (take 0, AB#6892); it must agree
        // with the unpaged query per origin as well.
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var districtRtIds = await CountParityCases.DistrictRtIdsAsync(repository, session);
        var unpaged = await CountParityCases.MunicipalitiesOfAsync(repository, session, districtRtIds);
        var countOnly = await CountParityCases.MunicipalitiesOfAsync(repository, session, districtRtIds, 0, 0);

        await session.CommitTransactionAsync();

        foreach (var (origin, resultSet) in unpaged)
        {
            Assert.Equal(resultSet.TotalCount, countOnly[origin].TotalCount);
            Assert.Empty(countOnly[origin].Items);
        }
    }

    /// <summary>
    ///     A count-only pipeline ends with <c>$count</c>, has no <c>$facet</c>, no paging, no sort and none of the
    ///     enrichment lookups of the item query: it can only ever return the one count document. The association
    ///     filter stages (existence or count lookup with their reshaping <c>$project</c>) stay: they narrow the count.
    /// </summary>
    internal static void AssertCountOnlyPipeline(IReadOnlyList<BsonDocument> stages,
        IReadOnlyList<BsonDocument> enrichmentStages)
    {
        Assert.NotEmpty(stages);
        Assert.Equal("$count", stages[^1].GetElement(0).Name);
        Assert.Single(stages, s => s.GetElement(0).Name == "$count");

        var forbidden = new[] { "$facet", "$skip", "$limit", "$sort" };
        foreach (var stage in stages)
        {
            Assert.DoesNotContain(stage.GetElement(0).Name, forbidden);
        }

        foreach (var enrichmentStage in enrichmentStages)
        {
            Assert.DoesNotContain(enrichmentStage, stages);
        }
    }

    internal static IReadOnlyList<BsonDocument> Render<TEntity, TOutput>(IReadOnlyList<IPipelineStageDefinition> stages)
    {
        if (stages.Count == 0)
        {
            return [];
        }

        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<TEntity>();
        return PipelineDefinition<TEntity, TOutput>.Create(stages)
            .Render(new RenderArgs<TEntity>(serializer, BsonSerializer.SerializerRegistry))
            .Documents
            .ToList();
    }
}
