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
///     A deep sorted page is hinted to the index that provides its sort (AB#9138), so MongoDB runs no plan trial: the
///     trial is what made a page at skip 50,000 over 909,000 entities take 56-67 s while its execution took ~90 ms. The
///     tests check the hint the page read uses and, by <c>explain</c> with that hint, that the plan reads the index in
///     sort order without a blocking SORT. Shallow pages, sorts no index provides, pages inside a transaction and
///     pipelines with stages around the match keep the planner's choice; a hint that names a dropped index is retried
///     without it.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class PagedQuerySortHintTests(ImportTestCkModelFixture fixture)
{
    private const int EntityCount = 3_000;
    private const int DeepSkip = 1_500;
    private const string NameIndex = "test_name_legacy";

    [Fact]
    public async Task DeepPage_SortedByAnIndexedAttribute_IsHintedToThatIndex_AndReadsItInOrder()
    {
        await WithSeededContinentsAsync(async (repository, raw, names) =>
        {
            var (page, executed) = await PageAsync(repository, SortByName(), DeepSkip, 10);

            Assert.Equal(NameIndex, executed.Hint);
            Assert.Equal(names.Skip(DeepSkip).Take(10), page.Select(c => c.Name));
            var plan = await ExplainAsync(raw, executed);
            Assert.Equal(NameIndex, plan.Index);
            Assert.False(plan.HasSort);

            // Descending reads the same index backwards.
            var (descending, executedDesc) = await PageAsync(repository,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Descending), DeepSkip, 10);
            Assert.Equal(NameIndex, executedDesc.Hint);
            Assert.Equal(names.AsEnumerable().Reverse().Skip(DeepSkip).Take(10), descending.Select(c => c.Name));
        });
    }

    [Fact]
    public async Task DeepPage_SortedByRtId_IsHintedToTheTypeAndIdIndex()
    {
        await WithSeededContinentsAsync(async (repository, raw, _) =>
        {
            var ids = await AllIdsAsync(raw);
            var (page, executed) = await PageAsync(repository,
                RtEntityQueryOptions.Create().SortOrder("RtId", SortOrders.Ascending), DeepSkip, 10);

            Assert.NotNull(executed.Hint);
            var hinted = (await IndexesAsync(raw)).Single(i => i["name"].AsString == executed.Hint);
            Assert.Equal(["ckTypeId", "_id"], hinted["key"].AsBsonDocument.Names.Take(2));
            Assert.Equal(ids.Skip(DeepSkip).Take(10), page.Select(c => c.RtId));
            Assert.False((await ExplainAsync(raw, executed)).HasSort);
        });
    }

    [Fact]
    public async Task ShallowPage_UnsortedPage_AndASortNoIndexProvides_KeepThePlannersChoice()
    {
        await WithSeededContinentsAsync(async (repository, _, names) =>
        {
            var (shallow, shallowExecuted) = await PageAsync(repository, SortByName(), 10, 10);
            Assert.Null(shallowExecuted.Hint);
            Assert.Equal(names.Skip(10).Take(10), shallow.Select(c => c.Name));

            var (_, unsorted) = await PageAsync(repository, RtEntityQueryOptions.Create(), DeepSkip, 10);
            Assert.Null(unsorted.Hint);

            // No index has {ckTypeId, rtCreationDateTime}: no hint, and the page is still right.
            static RtEntityQueryOptions ByCreationThenId() => RtEntityQueryOptions.Create()
                .SortOrder("RtCreationDateTime", SortOrders.Ascending).SortOrder("RtId", SortOrders.Ascending);
            var (unindexed, unindexedExecuted) = await PageAsync(repository, ByCreationThenId(), DeepSkip, 10);
            Assert.Null(unindexedExecuted.Hint);
            using var session = await repository.GetSessionAsync();
            var unpaged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                ByCreationThenId());
            Assert.Equal(unpaged.Items.Skip(DeepSkip).Take(10).Select(e => e.RtId), unindexed.Select(c => c.RtId));
        });
    }

    [Fact]
    public async Task DeepPage_InsideATransaction_IsNotHinted()
    {
        await WithSeededContinentsAsync(async (repository, _, names) =>
        {
            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            var executed = new List<SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline>();
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtContinent>(session,
                TestCkIds.RtCkContinentTypeId, SortByName());
            query.PipelineObserver = executed.Add;
            var result = await query.ExecuteQuery(session, DeepSkip, 10, includeTotalCount: false);
            await session.CommitTransactionAsync();

            Assert.Null(executed.Single(e => e.Purpose == "page").Hint);
            Assert.Equal(names.Skip(DeepSkip).Take(10), result.Items.Select(c => c.Name));
        });
    }

    [Fact]
    public async Task AHintNamingADroppedIndex_IsReadAgainWithoutTheHint()
    {
        await WithSeededContinentsAsync(async (repository, raw, names) =>
        {
            // The cache knows the index ...
            var (_, first) = await PageAsync(repository, SortByName(), DeepSkip, 10);
            Assert.Equal(NameIndex, first.Hint);

            // ... another process drops it, this one is not told.
            await raw.Indexes.DropOneAsync(NameIndex, TestContext.Current.CancellationToken);
            var (page, executed) = await PageAsync(repository, SortByName(), DeepSkip, 10);

            Assert.Null(executed.Hint); // the stale hint failed, the page was read again without it
            Assert.Equal(names.Skip(DeepSkip).Take(10), page.Select(c => c.Name));
            var (_, afterwards) = await PageAsync(repository, SortByName(), DeepSkip, 10);
            Assert.Null(afterwards.Hint); // the cache was refreshed
        });
    }

    /// <summary>
    ///     A deep page whose filter has an index of its own and few matches keeps the planner's choice (the filter's
    ///     index and a small sort beat the walk along the sort index): without a total the decision costs one capped
    ///     count, with the total the exact count runs before the page and is not repeated.
    /// </summary>
    [Fact]
    public async Task DeepPage_WithAFewMatchesOfAnIndexedFilter_KeepsThePlanner_AndCountsOnce()
    {
        await WithSeededContinentsAsync(async (repository, raw, names) =>
        {
            await raw.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { "rtCreationDateTime", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = "test_created" }), cancellationToken: TestContext.Current.CancellationToken);
            CollectionIndexCache.Invalidate(raw);

            static RtEntityQueryOptions Filtered() => SortByName()
                .FieldFilter("RtCreationDateTime", FieldFilterOperator.GreaterEqualThan, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var (page, executed, reads, total) = await PageWithReadsAsync(repository, Filtered(), DeepSkip, false);
            Assert.Null(executed.Hint);
            Assert.Equal(new[] { "capped count", "page" }, reads);
            Assert.Equal(names.Skip(DeepSkip).Take(10), page.Select(c => c.Name));

            (_, executed, reads, total) = await PageWithReadsAsync(repository, Filtered(), DeepSkip, true);
            Assert.Null(executed.Hint);
            Assert.Equal(new[] { "count", "page" }, reads);
            Assert.Equal(EntityCount, total);
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static async Task<(IReadOnlyList<RtContinent> Items,
        SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline Page, IReadOnlyList<string> Reads, long Total)>
        PageWithReadsAsync(TenantRepository repository, RtEntityQueryOptions options, int skip, bool includeTotalCount)
    {
        using var session = await repository.GetSessionAsync();
        var executed = new List<SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline>();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtContinent>(session,
            TestCkIds.RtCkContinentTypeId, options);
        query.PipelineObserver = executed.Add;
        var result = await query.ExecuteQuery(session, skip, 10, includeTotalCount);
        return (result.Items.ToList(), executed.Single(e => e.Purpose == "page"),
            executed.Select(e => e.Purpose).ToList(), result.TotalCount);
    }

    private static RtEntityQueryOptions SortByName() =>
        RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending);

    /// <summary>
    ///     Seeds continents with distinct names in a shuffled order, adds a CK-like legacy index on the name and a few
    ///     indexes that compete in the planner, and hands the sorted names to the test.
    /// </summary>
    private async Task WithSeededContinentsAsync(
        Func<TenantRepository, IMongoCollection<BsonDocument>, IReadOnlyList<string>, Task> test)
    {
        await fixture.ClearCollectionAsync();
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        try
        {
            var random = new Random(9138);
            var names = Enumerable.Range(0, EntityCount).Select(i => $"Continent-{i:D5}").ToList();
            using (var session = await repository.GetSessionAsync())
            {
                session.StartTransaction();
                var continents = new List<RtEntity>();
                foreach (var name in names.OrderBy(_ => random.Next()))
                {
                    var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
                    continent.RtId = OctoObjectId.GenerateNewId();
                    continent.Name = name;
                    continents.Add(continent);
                }

                await repository.InsertManyRtEntityAsync(session, TestCkIds.RtCkContinentTypeId, continents);
                await session.CommitTransactionAsync();
            }

            var raw = await RawCollectionAsync(repository);
            await raw.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { { "ckTypeId", 1 }, { "attributes.name", 1 }, { "rtState", 1 } },
                    new CreateIndexOptions { Name = NameIndex }),
                new CreateIndexModel<BsonDocument>(new BsonDocument { { "rtState", 1 }, { "attributes.name", 1 } },
                    new CreateIndexOptions { Name = "test_competing_state_name" }),
                new CreateIndexModel<BsonDocument>(new BsonDocument("attributes.name", 1),
                    new CreateIndexOptions { Name = "test_competing_name" })
            ], TestContext.Current.CancellationToken);
            CollectionIndexCache.Invalidate(raw);

            await test(repository, raw, names);
        }
        finally
        {
            await fixture.ClearCollectionAsync();
        }
    }

    private static async Task<IMongoCollection<BsonDocument>> RawCollectionAsync(TenantRepository repository)
    {
        using var session = await repository.GetSessionAsync();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session, TestCkIds.RtCkContinentTypeId,
            RtEntityQueryOptions.Create());
        var collection = query.GetMongoCollection();
        return collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
    }

    private static async Task<(IReadOnlyList<RtContinent> Items,
        SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline Page)> PageAsync(TenantRepository repository,
        RtEntityQueryOptions options, int skip, int take)
    {
        using var session = await repository.GetSessionAsync();
        var executed = new List<SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline>();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtContinent>(session,
            TestCkIds.RtCkContinentTypeId, options);
        query.PipelineObserver = executed.Add;
        var result = await query.ExecuteQuery(session, skip, take, includeTotalCount: false);
        return (result.Items.ToList(), executed.Single(e => e.Purpose == "page"));
    }

    private static async Task<List<BsonDocument>> IndexesAsync(IMongoCollection<BsonDocument> raw)
    {
        using var cursor = await raw.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static async Task<List<OctoObjectId>> AllIdsAsync(IMongoCollection<BsonDocument> raw) =>
        (await raw.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Project(new BsonDocument("_id", 1))
            .ToListAsync()).Select(d => new OctoObjectId(d["_id"].AsObjectId.ToByteArray())).ToList();

    /// <summary>The winning plan of the page read with its hint: the index it reads and whether it sorts in memory.</summary>
    private static async Task<(string? Index, bool HasSort)> ExplainAsync(IMongoCollection<BsonDocument> raw,
        SingleOriginQuery<OctoObjectId, RtContinent>.ExecutedPipeline executed)
    {
        var command = new BsonDocument
        {
            { "aggregate", raw.CollectionNamespace.CollectionName },
            { "pipeline", new BsonArray(executed.Stages) },
            { "cursor", new BsonDocument() }
        };
        if (executed.Hint != null)
        {
            command["hint"] = executed.Hint;
        }

        var explain = await raw.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", command }, { "verbosity", "queryPlanner" }
        });
        var plan = FindFirst(explain, "winningPlan") ?? throw new InvalidOperationException(explain.ToJson());
        var stages = new List<string>();
        string? index = null;
        Walk(plan, (name, value) =>
        {
            if (name == "stage")
            {
                stages.Add(value.AsString);
            }
            else if (name == "indexName" && index == null)
            {
                index = value.AsString;
            }
        });
        return (index, stages.Any(s => s is "SORT" or "SORT_KEY_GENERATOR"));
    }

    private static BsonValue? FindFirst(BsonValue node, string name)
    {
        switch (node)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    if (element.Name == name)
                    {
                        return element.Value;
                    }

                    if (FindFirst(element.Value, name) is { } found)
                    {
                        return found;
                    }
                }

                break;
            case BsonArray array:
                foreach (var element in array)
                {
                    if (FindFirst(element, name) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    private static void Walk(BsonValue node, Action<string, BsonValue> visit)
    {
        switch (node)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    visit(element.Name, element.Value);
                    Walk(element.Value, visit);
                }

                break;
            case BsonArray array:
                foreach (var element in array)
                {
                    Walk(element, visit);
                }

                break;
        }
    }
}
