using System.Diagnostics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
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
///     Measurement of the page hint (AB#9138, AC 1, 2 and the selective-filter risk): the engine's page pipeline over
///     <see cref="DefaultEntityCount" /> entities (override with <c>OCTO_TEST_PAGE_HINT_ENTITIES</c>), run once without
///     and once with the hint <see cref="SortIndexHint" /> chooses, median of a few runs, plus <c>explain</c>
///     (executionStats). The collection carries the indexes a CK type carries on sbeg: the System Entity indexes, a CK
///     Ascending index on the sorted attribute and a few competing ones, so that the planner has several candidates as
///     on sbeg (9 there). The numbers go to the <c>octo-progress</c> lines; nothing is asserted about time
///     (<c>Category=Performance</c>, not in the CI).
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
[Trait("Category", "Performance")]
[Trait("Category", "LoadSensitive")]
public class PagedQuerySortHintPerformanceTests(ImportTestCkModelFixture fixture)
{
    private const string EntityCountVariable = "OCTO_TEST_PAGE_HINT_ENTITIES";
    private const int DefaultEntityCount = 900_000;
    private const int PageSize = 10;
    private const int MeasuredRuns = 3;
    private static readonly DateTime Start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DeepSortedPage_WithAndWithoutTheHint()
    {
        var entityCount = int.TryParse(Environment.GetEnvironmentVariable(EntityCountVariable), out var n)
            ? n
            : DefaultEntityCount;
        await fixture.ClearCollectionAsync();
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        try
        {
            var raw = await LoadAsync(repository, entityCount);
            var middle = Start.AddMinutes(entityCount / 2.0);

            RtEntityQueryOptions ById() => RtEntityQueryOptions.Create().SortOrder("RtId", SortOrders.Ascending);
            foreach (var skip in new[] { 0, 100, 1_000, 10_000, 50_000 })
            {
                await MeasureAsync(repository, raw, "sort rtId", ById(), skip, entityCount);
            }

            await MeasureAsync(repository, raw, "sort Name (CK index)",
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 50_000, entityCount);
            await MeasureAsync(repository, raw, "sort Name desc (CK index)",
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Descending), 50_000, entityCount);
            await MeasureAsync(repository, raw, "filter rtCreationDateTime >= middle (half), sort rtId",
                ById().FieldFilter("RtCreationDateTime", FieldFilterOperator.GreaterEqualThan, middle), 50_000,
                entityCount);
            await MeasureAsync(repository, raw, "selective filter Name like 'Continent-001*' (~1 %), sort rtId",
                ById().FieldFilter("Name", FieldFilterOperator.Like, "Continent-001*"), 2_000, entityCount);
            // The risk case: a filter with an index of its own and few matches. Without the hint the planner can read
            // the matches through that index and sort them in memory; the hint walks the sort index instead.
            foreach (var (matches, skip) in new[] { (2_000, 1_000), (20_000, 10_000), (100_000, 50_000) })
            {
                var from = middle;
                var to = middle.AddMinutes(matches);
                RtEntityQueryOptions Filtered() => ById()
                    .FieldFilter("RtCreationDateTime", FieldFilterOperator.GreaterEqualThan, from)
                    .FieldFilter("RtCreationDateTime", FieldFilterOperator.LessThan, to);
                var label = $"selective indexed filter rtCreationDateTime range ({matches:N0} matches), sort rtId";
                await MeasureAsync(repository, raw, label, Filtered(), skip, entityCount);
                await MeasureEngineAsync(repository, label, Filtered, skip, entityCount);
            }

            await MeasureEngineAsync(repository, "sort rtId, no filter", ById, 50_000, entityCount);

            await MeasureAsync(repository, raw, "unsorted (reference, never hinted)", RtEntityQueryOptions.Create(),
                50_000, entityCount);
        }
        finally
        {
            await fixture.ClearCollectionAsync();
        }
    }

    private static async Task MeasureAsync(TenantRepository repository, IMongoCollection<BsonDocument> raw,
        string label, RtEntityQueryOptions options, int skip, int entityCount)
    {
        List<BsonDocument> stages;
        string? hint;
        using (var session = await repository.GetSessionAsync())
        {
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
                TestCkIds.RtCkContinentTypeId, options);
            stages = CountOnlyParityTests.Render<RtEntity, RtEntity>(
                query.CreatePagePipelineStageDefinitions(skip, PageSize)).ToList();
            hint = query.SortKeys.Count == 0
                ? null
                : SortIndexHint.Choose(await CollectionIndexCache.GetAsync(query.GetMongoCollection()), query.SortKeys)
                    ?.Name;
        }

        foreach (var (version, versionHint) in new[] { ("planner", (string?)null), ("hint", hint) })
        {
            if (version == "hint" && versionHint == null)
            {
                RunProgress.Report($"AB#9138 perf [{entityCount:N0}] {label}, skip {skip:N0}: no index provides the sort - no hint");
                continue;
            }

            var plan = await ExplainAsync(raw, stages, versionHint);
            var times = new List<double>();
            var returned = 0;
            for (var run = 0; run < MeasuredRuns; run++)
            {
                var watch = Stopwatch.StartNew();
                var aggregateOptions = versionHint == null ? new AggregateOptions() : new AggregateOptions { Hint = versionHint };
                var page = await raw.Aggregate<BsonDocument>(stages, aggregateOptions).ToListAsync();
                times.Add(watch.Elapsed.TotalMilliseconds);
                returned = page.Count;
            }

            times.Sort();
            RunProgress.Report($"AB#9138 perf [{entityCount:N0}] {label}, skip {skip:N0}, {version}" +
                               $"{(versionHint == null ? "" : " " + versionHint)}: median {times[times.Count / 2]:F0} ms " +
                               $"(min {times[0]:F0}, max {times[^1]:F0}), {returned} docs; {plan}");
        }
    }

    /// <summary>
    ///     The engine's own page read (<c>ExecuteQuery</c>, the hint decision included) without and with the total:
    ///     median time, the hint it chose and the reads it made; plus the cost of the capped count alone.
    /// </summary>
    private static async Task MeasureEngineAsync(TenantRepository repository, string label,
        Func<RtEntityQueryOptions> options, int skip, int entityCount)
    {
        foreach (var includeTotalCount in new[] { false, true })
        {
            var times = new List<double>();
            List<SingleOriginQuery<OctoObjectId, RtEntity>.ExecutedPipeline> executed = [];
            for (var run = 0; run < MeasuredRuns; run++)
            {
                using var session = await repository.GetSessionAsync();
                var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
                    TestCkIds.RtCkContinentTypeId, options());
                executed = [];
                query.PipelineObserver = executed.Add;
                var watch = Stopwatch.StartNew();
                await query.ExecuteQuery(session, skip, PageSize, includeTotalCount);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            var page = executed.Single(e => e.Purpose == "page");
            RunProgress.Report($"AB#9138 engine [{entityCount:N0}] {label}, skip {skip:N0}, totalCount " +
                               $"{includeTotalCount}: median {times[times.Count / 2]:F0} ms (min {times[0]:F0}, " +
                               $"max {times[^1]:F0}); hint {page.Hint ?? "-"}; reads " +
                               string.Join(",", executed.Select(e => e.Purpose)));
        }

        using (var session = await repository.GetSessionAsync())
        {
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
                TestCkIds.RtCkContinentTypeId, options());
            var times = new List<double>();
            long counted = 0;
            for (var run = 0; run < MeasuredRuns; run++)
            {
                var watch = Stopwatch.StartNew();
                counted = await query.ExecuteCappedCount(session, SortIndexHint.MinMatches);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            RunProgress.Report($"AB#9138 engine [{entityCount:N0}] {label}: capped count (limit " +
                               $"{SortIndexHint.MinMatches:N0}) = {counted:N0} in {times[times.Count / 2]:F0} ms");
        }
    }

    /// <summary>
    ///     The explain (executionStats) of the page: winning plan stages, index, keys and documents examined, number of
    ///     candidate plans the planner tried, server time.
    /// </summary>
    private static async Task<string> ExplainAsync(IMongoCollection<BsonDocument> raw, List<BsonDocument> stages,
        string? hint)
    {
        var command = new BsonDocument
        {
            { "aggregate", raw.CollectionNamespace.CollectionName },
            { "pipeline", new BsonArray(stages) },
            { "cursor", new BsonDocument() }
        };
        if (hint != null)
        {
            command["hint"] = hint;
        }

        var explain = await raw.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", command }, { "verbosity", "allPlansExecution" }
        });
        var winningPlan = FindFirst(explain, "winningPlan") as BsonDocument;
        var rejected = FindFirst(explain, "rejectedPlans") as BsonArray;
        var stagesSeen = new List<string>();
        var indexes = new List<string>();
        if (winningPlan != null)
        {
            Walk(winningPlan, (name, value) =>
            {
                if (name == "stage")
                {
                    stagesSeen.Add(value.AsString);
                }
                else if (name == "indexName")
                {
                    indexes.Add(value.AsString);
                }
            });
        }

        var stats = FindFirst(explain, "executionStats") as BsonDocument;
        return $"plan {string.Join(">", stagesSeen)}; index {string.Join(",", indexes.Distinct())}; " +
               $"candidates {1 + (rejected?.Count ?? 0)}; keys {stats?.GetValue("totalKeysExamined", "?")}, " +
               $"docs {stats?.GetValue("totalDocsExamined", "?")}, server {stats?.GetValue("executionTimeMillis", "?")} ms";
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

    /// <summary>
    ///     Loads the continents straight into the collection (template from one entity the repository wrote), names in a
    ///     shuffled order relative to the ids, creation times one minute apart, and adds the indexes.
    /// </summary>
    private static async Task<IMongoCollection<BsonDocument>> LoadAsync(TenantRepository repository, int entityCount)
    {
        var templateId = OctoObjectId.GenerateNewId();
        IMongoCollection<RtEntity> collection;
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
            continent.RtId = templateId;
            continent.Name = "template";
            await repository.InsertOneRtEntityAsync(session, TestCkIds.RtCkContinentTypeId, continent);
            await session.CommitTransactionAsync();
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
                TestCkIds.RtCkContinentTypeId, RtEntityQueryOptions.Create());
            collection = query.GetMongoCollection();
        }

        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var template = await raw.Find(new BsonDocument("_id", new ObjectId(templateId.ToByteArray()))).FirstAsync();
        await raw.DeleteManyAsync(new BsonDocument());

        var watch = Stopwatch.StartNew();
        var random = new Random(9138);
        var order = Enumerable.Range(0, entityCount).OrderBy(_ => random.Next()).ToArray();
        for (var offset = 0; offset < entityCount; offset += 10_000)
        {
            var batch = new List<BsonDocument>();
            for (var i = offset; i < Math.Min(offset + 10_000, entityCount); i++)
            {
                var document = template.DeepClone().AsBsonDocument;
                document["_id"] = ObjectId.GenerateNewId();
                document["rtCreationDateTime"] = Start.AddMinutes(i);
                document["attributes"].AsBsonDocument["name"] = $"Continent-{order[i]:D7}";
                batch.Add(document);
            }

            await raw.InsertManyAsync(batch);
        }

        await raw.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { "attributes.name", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = "TestContinent_0" }),
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { "rtCreationDateTime", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = "TestContinent_1" }),
            new CreateIndexModel<BsonDocument>(new BsonDocument { { "rtState", 1 }, { "attributes.name", 1 } },
                new CreateIndexOptions { Name = "perf_competing_0" }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("rtChangedDateTime", 1),
                new CreateIndexOptions { Name = "perf_competing_1" })
        ]);
        CollectionIndexCache.Invalidate(raw);
        RunProgress.Report($"AB#9138 perf: loaded {entityCount:N0} continents in {watch.Elapsed.TotalSeconds:F1} s");
        return raw;
    }
}
