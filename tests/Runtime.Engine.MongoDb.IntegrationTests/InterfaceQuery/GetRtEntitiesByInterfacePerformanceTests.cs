using System.Diagnostics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>
///     Performance check of the interface query (AB#7019) against the budget decided in the spike AB#6930
///     (Gerald, 2026-10-19, option A). Same style as the dispatcher performance test: fixed seed data, warm-up, the
///     median / p95 of repeated runs, thresholds as ratios and generous ceilings, an unindexed sort that is measured and
///     printed but never asserted.
///     <para>
///         <b>Size.</b> Default 100 000 entities over 30 collection roots (CI-friendly, a few seconds of load). The
///         1M run is opt-in: set <c>OCTO_TEST_INTERFACE_QUERY_ENTITIES=1000000</c>. Exclude the test from a quick loop with
///         <c>--filter-not-trait "Category=Performance"</c>. The measured numbers are written to the test run's
///         diagnostic output (<c>octo-progress</c> lines) and into the engine-mongodb <c>CLAUDE.md</c>.
///     </para>
/// </summary>
[Collection(InterfaceQueryCollection.Name)]
[Trait("Category", "Performance")]
public class GetRtEntitiesByInterfacePerformanceTests(InterfaceQueryFixture fixture)
{
    // ---- Budget of the decision AB#6930 (2026-10-19, Gerald, option A), as named constants ---------------------------

    /// <summary>(a) Implementers in ONE collection: at most 1.25x of a type query that returns the same rows.</summary>
    private const double SameCollectionRatioCeiling = 1.25;

    /// <summary>
    ///     Timer noise allowance of the ratio check: at a median of a millisecond or two a ratio is meaningless, so the
    ///     ceiling is <c>max(ratio * type query, type query + this)</c>. Not part of the decision.
    /// </summary>
    private const double SameCollectionAbsoluteSlackMs = 5;

    /// <summary>(b) Several collections, sort field indexed: first-page p95 at most 100 ms (30 collections, 1M entities).</summary>
    private const double IndexedMultiCollectionFirstPageP95CeilingMs = 100;

    /// <summary>The number of collection roots of the budget (b).</summary>
    private const int CollectionRoots = 30;

    private const string EntityCountVariable = "OCTO_TEST_INTERFACE_QUERY_ENTITIES";
    private const int DefaultEntityCount = 100_000;
    private const int WarmUpRuns = 3;
    private const int MeasuredRuns = 20;
    private const int PageSize = 50;
    private const int Seed = 7019;

    private static readonly RtCkId<CkInterfaceId> Named = new("Perf/Named");
    private static readonly RtCkId<CkInterfaceId> Solo = new("Perf/Solo");

    [Fact]
    public async Task IndexedSort_FirstPage_StaysWithinTheDecidedBudget()
    {
        var entityCount = int.TryParse(Environment.GetEnvironmentVariable(EntityCountVariable), out var configured) &&
                          configured > 0
            ? configured
            : DefaultEntityCount;

        await fixture.ResetAsync();
        var systemContext = fixture.GetSystemContext();
        await systemContext.ImportCkModelAsync(InterfaceQueryWideModel.Build(
            await fixture.GetInstalledSystemIdAsync(), "Perf", CollectionRoots, indexName: true, soloInterface: true));
        var repository = (TenantRepository)systemContext.GetTenantRepository();

        var loadWatch = Stopwatch.StartNew();
        await LoadAsync(repository, entityCount);
        RunProgress.Report($"interface-query perf: loaded {entityCount:N0} entities over {CollectionRoots} collections " +
                           $"in {loadWatch.Elapsed.TotalSeconds:F1}s");
        await ReportIndexesAsync(repository);

        var nameAscending = RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending);
        var rankAscending = RtEntityQueryOptions.Create().SortOrder("Rank", SortOrders.Ascending);

        foreach (var inTransaction in new[] { true, false })
        {
            var mode = inTransaction ? "transaction (sequential)" : "no transaction (parallel)";

            // (b) several collections, sort field indexed: first page p95
            var indexed = await MeasureAsync(repository, Named, nameAscending, 0, PageSize, false, inTransaction);
            var indexedDeep = await MeasureAsync(repository, Named, nameAscending, 450, PageSize, false, inTransaction);
            var indexedCounted = await MeasureAsync(repository, Named, nameAscending, 0, PageSize, true, inTransaction);
            RunProgress.Report($"interface-query perf [{entityCount:N0} entities, {CollectionRoots} collections, {mode}] " +
                               $"indexed first page: median {indexed.Median:F1} ms, p95 {indexed.P95:F1} ms | " +
                               $"skip 450: median {indexedDeep.Median:F1} ms, p95 {indexedDeep.P95:F1} ms | " +
                               $"with count: median {indexedCounted.Median:F1} ms, p95 {indexedCounted.P95:F1} ms");
            Assert.True(indexed.P95 <= IndexedMultiCollectionFirstPageP95CeilingMs,
                $"indexed first page p95 {indexed.P95:F1} ms is above the budget of " +
                $"{IndexedMultiCollectionFirstPageP95CeilingMs} ms (decision AB#6930, {mode}).");

            // (c) sort field not indexed: no latency promise - measured and printed, not asserted
            var unindexed = await MeasureAsync(repository, Named, rankAscending, 0, PageSize, false, inTransaction, 5, 3);
            RunProgress.Report($"interface-query perf [{entityCount:N0} entities, {CollectionRoots} collections, {mode}] " +
                               $"UNINDEXED sort (Rank), first page: median {unindexed.Median:F0} ms, p95 {unindexed.P95:F0} ms " +
                               "(printed, not asserted: O(matching rows))");

            // (a) the implementers live in ONE collection: against the type query that returns the same rows
            foreach (var (label, options) in new[] { ("indexed sort (Name)", nameAscending), ("unindexed sort (Rank)", rankAscending) })
            {
                var runs = label.StartsWith("indexed", StringComparison.Ordinal) ? MeasuredRuns : 8;
                var viaInterface = await MeasureAsync(repository, Solo, options, 0, PageSize, true, inTransaction, WarmUpRuns, runs);
                var viaType = await MeasureTypeQueryAsync(repository, new RtCkId<CkTypeId>("Perf/T01"), options, 0,
                    PageSize, inTransaction, WarmUpRuns, runs);
                var ceiling = Math.Max(SameCollectionRatioCeiling * viaType.Median,
                    viaType.Median + SameCollectionAbsoluteSlackMs);
                RunProgress.Report($"interface-query perf [{mode}] one collection, {label}: interface median " +
                                   $"{viaInterface.Median:F1} ms vs type query {viaType.Median:F1} ms " +
                                   $"(ratio {viaInterface.Median / Math.Max(viaType.Median, 0.001):F2}, ceiling {ceiling:F1} ms)");
                Assert.True(viaInterface.Median <= ceiling,
                    $"one collection, {label}: interface query median {viaInterface.Median:F1} ms is above " +
                    $"{SameCollectionRatioCeiling}x of the type query ({viaType.Median:F1} ms), decision AB#6930, {mode}.");
            }
        }
    }

    // ---- measuring --------------------------------------------------------------------------------------------------

    private readonly record struct Timing(double Median, double P95);

    private static async Task<Timing> MeasureAsync(ITenantRepository repository, RtCkId<CkInterfaceId> ckInterfaceId,
        RtEntityQueryOptions options, int skip, int take, bool count, bool inTransaction, int warmUp = WarmUpRuns,
        int runs = MeasuredRuns)
    {
        return await TimeAsync(async () =>
        {
            using var session = await repository.GetSessionAsync();
            if (inTransaction)
            {
                session.StartTransaction();
            }

            var result = await repository.GetRtEntitiesByInterfaceAsync(session, ckInterfaceId, options, skip, take, count);
            if (inTransaction)
            {
                await session.CommitTransactionAsync();
            }

            Assert.Equal(take, result.Items.Count);
        }, warmUp, runs);
    }

    private static async Task<Timing> MeasureTypeQueryAsync(ITenantRepository repository, RtCkId<CkTypeId> ckTypeId,
        RtEntityQueryOptions options, int skip, int take, bool inTransaction, int warmUp, int runs)
    {
        return await TimeAsync(async () =>
        {
            using var session = await repository.GetSessionAsync();
            if (inTransaction)
            {
                session.StartTransaction();
            }

            var result = await repository.GetRtEntitiesByTypeAsync(session, ckTypeId, options, skip, take);
            if (inTransaction)
            {
                await session.CommitTransactionAsync();
            }

            Assert.Equal(take, result.Items.Count());
        }, warmUp, runs);
    }

    private static async Task<Timing> TimeAsync(Func<Task> work, int warmUp, int runs)
    {
        for (var i = 0; i < warmUp; i++)
        {
            await work();
        }

        var samples = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            await work();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return new Timing(samples[samples.Count / 2], samples[(int)Math.Ceiling(0.95 * samples.Count) - 1]);
    }

    // ---- seed data ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Loads <paramref name="entityCount" /> entities evenly over the 30 roots straight into the collections: one
    ///     entity is created through the repository and its stored document is the template of all others (so the
    ///     documents have exactly the shape the engine writes), the names are random strings of a fixed seed.
    /// </summary>
    private static async Task LoadAsync(TenantRepository repository, int entityCount)
    {
        await InterfaceQuerySeed.InsertAsync(repository, "T01", 1, "template", 1, modelName: "Perf");
        var rootCollection = repository.DataSource
            .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>("Perf/T01")).GetMongoCollection();
        var template = await rootCollection.Database.GetCollection<BsonDocument>(rootCollection.CollectionNamespace.CollectionName)
            .Find(new BsonDocument()).FirstAsync();

        var random = new Random(Seed);
        var perRoot = entityCount / CollectionRoots;
        for (var root = 1; root <= CollectionRoots; root++)
        {
            var typeName = $"Perf/T{root:D2}";
            var collection = repository.DataSource
                .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>(typeName)).GetMongoCollection();
            var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
            var count = root == CollectionRoots ? entityCount - perRoot * (CollectionRoots - 1) : perRoot;
            if (root == 1)
            {
                count--; // the template stays in
            }

            for (var offset = 0; offset < count; offset += 10_000)
            {
                var batch = new List<BsonDocument>(Math.Min(10_000, count - offset));
                for (var i = offset; i < Math.Min(offset + 10_000, count); i++)
                {
                    var document = template.DeepClone().AsBsonDocument;
                    document["_id"] = ObjectId.GenerateNewId();
                    document["ckTypeId"] = typeName;
                    var attributes = document["attributes"].AsBsonDocument;
                    attributes["name"] = RandomName(random);
                    attributes["rank"] = random.Next(0, 1_000_000);
                    batch.Add(document);
                }

                await raw.InsertManyAsync(batch);
            }
        }
    }

    private static string RandomName(Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        return string.Create(10, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[r.Next(alphabet.Length)];
            }
        });
    }

    private static async Task ReportIndexesAsync(TenantRepository repository)
    {
        var collection = repository.DataSource
            .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>("Perf/T01")).GetMongoCollection();
        var indexes = await collection.Indexes.ListAsync();
        var names = (await indexes.ToListAsync()).Select(i => i["key"].ToJson());
        RunProgress.Report($"interface-query perf: indexes of Perf/T01: {string.Join(" | ", names)}");
    }
}
