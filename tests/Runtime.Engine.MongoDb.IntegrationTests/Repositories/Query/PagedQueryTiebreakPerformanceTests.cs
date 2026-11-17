using System.Diagnostics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     Index impact of the <c>_id</c> tiebreak of a page (AB#8960, AC 5). Measures the page pipeline without the
///     tiebreak (caller's sort only, no sort without one - before AB#8960, and on collections the paging gate keeps on
///     the legacy order) and with it (caller's keys + <c>_id</c>, <c>_id</c> alone without one) on the same data:
///     <c>explain</c> (executionStats) - winning plan stages, index, keys and documents examined, whether the sort
///     spilled to disk - and the median of repeated runs, for the first page and a deep page.
///     <para>
///         Data: <see cref="DefaultEntityCount" /> continents (override with <c>OCTO_TEST_PAGED_TIEBREAK_ENTITIES</c>),
///         100 distinct names (heavy ties), a ~4 KB payload attribute so that a deep top-k sort exceeds the 100 MB sort
///         memory limit. Indexes: a CK Ascending index on Name in the legacy shape <c>{ckTypeId, name, rtState}</c>, then
///         instead in the tiebreak shape <c>{ckTypeId, name, _id, rtState}</c>. The numbers are written as
///         <c>octo-progress</c> lines; nothing is asserted about time (not in the CI, <c>Category=Performance</c>).
///     </para>
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
[Trait("Category", "Performance")]
[Trait("Category", "LoadSensitive")]
public class PagedQueryTiebreakPerformanceTests(ImportTestCkModelFixture fixture)
{
    private const string EntityCountVariable = "OCTO_TEST_PAGED_TIEBREAK_ENTITIES";
    private const int DefaultEntityCount = 100_000;
    private const int DistinctNames = 100;
    private const int PayloadBytes = 4_096;
    private const int PageSize = 50;
    private const int DeepSkip = 50_000;
    private const int MeasuredRuns = 5;

    [Fact]
    public async Task PagePipeline_BeforeAndAfterTheTiebreak_ExplainAndTiming()
    {
        var entityCount = int.TryParse(Environment.GetEnvironmentVariable(EntityCountVariable), out var n)
            ? n
            : DefaultEntityCount;
        await fixture.ClearCollectionAsync();
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        try
        {
            var raw = await LoadAsync(repository, entityCount);
            var deepSkip = Math.Min(DeepSkip, entityCount / 2);

            // A CK Ascending index on Name as an engine before AB#8960 built it (legacy shape).
            await raw.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { "attributes.name", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = "perf_legacy" }), cancellationToken: TestContext.Current.CancellationToken);
            foreach (var skip in new[] { 0, deepSkip })
            {
                await MeasureAsync(repository, raw, "legacy index", sorted: true, skip, entityCount);
                await MeasureAsync(repository, raw, "legacy index", sorted: false, skip, entityCount);
            }

            // The same index after UpgradeTiebreakIndexesAsync (tiebreak shape); "without" is an engine before AB#8960
            // after a rollback.
            await raw.Indexes.DropOneAsync("perf_legacy", TestContext.Current.CancellationToken);
            await raw.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { "attributes.name", 1 }, { "_id", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = "perf_tiebreak" }), cancellationToken: TestContext.Current.CancellationToken);
            foreach (var skip in new[] { 0, deepSkip })
            {
                await MeasureAsync(repository, raw, "tiebreak index", sorted: true, skip, entityCount);
                await MeasureAsync(repository, raw, "tiebreak index", sorted: false, skip, entityCount);
            }
        }
        finally
        {
            await fixture.ClearCollectionAsync();
        }
    }

    private static async Task MeasureAsync(TenantRepository repository, IMongoCollection<BsonDocument> raw,
        string label, bool sorted, int skip, int entityCount)
    {
        var options = sorted
            ? RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending)
            : RtEntityQueryOptions.Create();
        List<BsonDocument> without;
        List<BsonDocument> with;
        using (var session = await repository.GetSessionAsync())
        {
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
                TestCkIds.RtCkContinentTypeId, options);
            // Without the tiebreak: the page before AB#8960 (caller's sort only, no sort without one).
            without = CountOnlyParityTests.Render<RtEntity, RtEntity>(query.CreatePagePipelineStageDefinitions(skip,
                PageSize, idTiebreak: false)).ToList();
            with = CountOnlyParityTests.Render<RtEntity, RtEntity>(query.CreatePagePipelineStageDefinitions(skip,
                PageSize, idTiebreak: true)).ToList();
        }

        label = $"{label}, {(sorted ? "sort Name" : "no sort")}";
        foreach (var (version, pipeline) in new[] { ("without tiebreak", without), ("with tiebreak", with) })
        {
            var plan = await ExplainAsync(raw, pipeline);
            var times = new List<double>();
            var returned = 0;
            for (var run = 0; run < MeasuredRuns; run++)
            {
                var watch = Stopwatch.StartNew();
                var page = await raw.Aggregate<BsonDocument>(pipeline).ToListAsync();
                times.Add(watch.Elapsed.TotalMilliseconds);
                returned = page.Count;
            }

            Assert.Equal(PageSize, returned);
            times.Sort();
            RunProgress.Report($"AB#8960 perf [{entityCount:N0} entities] {label}, skip {skip:N0}, {version}: " +
                               $"median {times[times.Count / 2]:F0} ms (min {times[0]:F0}, max {times[^1]:F0}); " +
                               $"sort {pipeline.FirstOrDefault(s => s.GetElement(0).Name == "$sort")?["$sort"].ToJson() ?? "-"}; {plan}");
        }
    }

    /// <summary>
    ///     The explain (executionStats) of an aggregation, reduced to: winning plan stages, indexes, keys/documents
    ///     examined, server execution time and whether a sort spilled to disk.
    /// </summary>
    private static async Task<string> ExplainAsync(IMongoCollection<BsonDocument> raw, List<BsonDocument> pipeline)
    {
        var command = new BsonDocument
        {
            {
                "explain", new BsonDocument
                {
                    { "aggregate", raw.CollectionNamespace.CollectionName },
                    { "pipeline", new BsonArray(pipeline) },
                    { "cursor", new BsonDocument() }
                }
            },
            { "verbosity", "executionStats" }
        };
        var explain = await raw.Database.RunCommandAsync<BsonDocument>(command);

        var winningPlan = Find(explain, "winningPlan") as BsonDocument;
        var stages = new List<string>();
        var indexes = new List<string>();
        if (winningPlan != null)
        {
            Walk(winningPlan, (name, value) =>
            {
                if (name == "stage")
                {
                    stages.Add(value.AsString);
                }
                else if (name == "indexName")
                {
                    indexes.Add(value.AsString);
                }
            });
        }

        var stats = Find(explain, "executionStats") as BsonDocument;
        var spilled = false;
        Walk(explain, (name, value) =>
        {
            if ((name == "usedDisk" && value.IsBoolean && value.AsBoolean) ||
                (name is "spills" or "spilledRecords" && value.IsNumeric && value.ToDouble() > 0))
            {
                spilled = true;
            }
        });

        return $"plan {string.Join(">", stages)}; index {(indexes.Count == 0 ? "-" : string.Join(",", indexes.Distinct()))}; " +
               $"keys {stats?.GetValue("totalKeysExamined", "?")}, docs {stats?.GetValue("totalDocsExamined", "?")}, " +
               $"server {stats?.GetValue("executionTimeMillis", "?")} ms; sort spilled to disk: {spilled}";
    }

    private static BsonValue? Find(BsonValue node, string name)
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

                    var found = Find(element.Value, name);
                    if (found != null)
                    {
                        return found;
                    }
                }

                break;
            case BsonArray array:
                foreach (var element in array)
                {
                    var found = Find(element, name);
                    if (found != null)
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
    ///     Loads the continents straight into the collection: one is created through the repository and its stored
    ///     document is the template of all others (the shape the engine writes), inserted in batches.
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

            // The collection the page pipeline of the type query reads.
            var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
                TestCkIds.RtCkContinentTypeId, RtEntityQueryOptions.Create());
            collection = query.GetMongoCollection();
        }

        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var templateFilter = new BsonDocument("_id", BsonValue.Create(collection.Find(e => e.RtId == templateId)
            .Project(Builders<RtEntity>.Projection.Include("_id")).First()["_id"]));
        var template = await raw.Find(templateFilter).FirstAsync();
        await raw.DeleteOneAsync(templateFilter);

        var watch = Stopwatch.StartNew();
        var random = new Random(8960);
        var payload = new string('x', PayloadBytes);
        for (var offset = 0; offset < entityCount; offset += 5_000)
        {
            var batch = new List<BsonDocument>();
            for (var i = offset; i < Math.Min(offset + 5_000, entityCount); i++)
            {
                var document = template.DeepClone().AsBsonDocument;
                document["_id"] = ObjectId.GenerateNewId();
                var attributes = document["attributes"].AsBsonDocument;
                attributes["name"] = $"name-{random.Next(DistinctNames):D3}";
                attributes["payload"] = payload;
                batch.Add(document);
            }

            await raw.InsertManyAsync(batch);
        }

        RunProgress.Report($"AB#8960 perf: loaded {entityCount:N0} continents in {watch.Elapsed.TotalSeconds:F1} s");
        return raw;
    }
}
