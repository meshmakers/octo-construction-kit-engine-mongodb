using System.Diagnostics;
using System.Globalization;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataDownsampling;

/// <summary>
///     AB#8993: downsampling a time-range archive of ~3500 entities with 15-minute windows over 400 ids and 7 days stays
///     under 5 s. prod-2 tenant <c>sbeg</c> (129 M rows, about 400 ids per call) reported 20-65 s; the analysis found the
///     SQL shape is not the cost driver (IN list, DATE_BIN GROUP BY, the §7 containment and the phase snap together take
///     ~50 ms here, independent of the history outside the window). This test keeps it that way: the requested shapes of
///     the MeshBoard line chart (limit 800 and 200, a relative window that is not on a window start) and ten parallel
///     calls as the board sends them, each within the budget and with every bin complete.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
[Trait("Category", "Performance")]
public class DownsamplingManyIdsPerformanceTests(CrateStagingCopyFixture fixture)
{
    /// <summary>The budget of AB#8993 (acceptance criterion "returns in under 5 s"), per call.</summary>
    private const long BudgetMs = 5_000;

    private const string EntityCountVariable = "OCTO_TEST_DOWNSAMPLING_ENTITIES";
    private const int DefaultEntityCount = 3_500;
    private const int SeedDays = 8;
    private const int IdsPerCall = 400;
    private const int ParallelCalls = 10;
    private const int IdsPerParallelCall = 350;
    private const int MeasuredRuns = 3;

    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly DateTime SeedEnd = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private static string Id(int i) => "67000000000000" + i.ToString("D10", CultureInfo.InvariantCulture);

    [Fact]
    public async Task SevenDaysOverManyIds_StaysWithinFiveSecondsPerCall()
    {
        var entities = int.TryParse(Environment.GetEnvironmentVariable(EntityCountVariable), CultureInfo.InvariantCulture, out var n)
            ? n
            : DefaultEntityCount;
        // The id sets below must be distinct entities: 400 spread ids and 10 disjoint groups of 350. Fewer entities would
        // repeat ids (or overlap the groups) while the assertions still count every id, so refuse them up front.
        Assert.True(entities >= ParallelCalls * IdsPerParallelCall,
            $"{EntityCountVariable}={entities}: at least {ParallelCalls * IdsPerParallelCall} entities are needed " +
            $"({ParallelCalls} disjoint groups of {IdsPerParallelCall} ids, {IdsPerCall} distinct ids per call)");
        var (rtId, repository) = await CreateSeededArchiveAsync(entities);

        // A relative "last 7 days" window as the board sends it: To is not on a window start, so the axis snaps (AB#8962).
        var to = SeedEnd.AddMinutes(-7);
        var from = to.AddDays(-7);
        var ids = Enumerable.Range(0, IdsPerCall).Select(i => OctoObjectId.Parse(Id(i * (entities / IdsPerCall)))).ToList();

        foreach (var limit in new[] { 800, 200 })
        {
            for (var run = 0; run <= MeasuredRuns; run++)
            {
                var watch = Stopwatch.StartNew();
                var rows = await DownsampleAsync(repository, rtId, from, to, limit, ids);
                watch.Stop();
                RunProgress.Report($"AB#8993 downsampling limit {limit}, {IdsPerCall} ids, run {run}: {watch.ElapsedMilliseconds} ms, {rows.Count} bins");

                // limit 800: 15-minute bins (673 incl. the snapped first bin); limit 200: 45-minute bins (3 windows each).
                AssertComplete(rows, limit == 800 ? 673 : 225, IdsPerCall, limit == 800 ? 1 : 3);
                Assert.True(watch.ElapsedMilliseconds < BudgetMs,
                    $"downsampling limit {limit} over {IdsPerCall} ids took {watch.ElapsedMilliseconds} ms (budget {BudgetMs} ms)");
            }
        }

        // Board shape: one call per OBIS group, in parallel, disjoint id sets (entities >= 3500, checked above).
        var parallel = Stopwatch.StartNew();
        var calls = await Task.WhenAll(Enumerable.Range(0, ParallelCalls).Select(async g =>
        {
            var set = Enumerable.Range(0, IdsPerParallelCall)
                .Select(i => OctoObjectId.Parse(Id(g * IdsPerParallelCall + i))).ToList();
            var watch = Stopwatch.StartNew();
            var rows = await DownsampleAsync(repository, rtId, from, to, 800, set);
            return (rows, watch.ElapsedMilliseconds);
        }));
        RunProgress.Report($"AB#8993 {ParallelCalls} parallel calls x {IdsPerParallelCall} ids: total {parallel.ElapsedMilliseconds} ms, " +
                           $"each [{string.Join(", ", calls.Select(c => c.ElapsedMilliseconds))}] ms");
        foreach (var (rows, elapsed) in calls)
        {
            AssertComplete(rows, 673, IdsPerParallelCall, 1);
            Assert.True(elapsed < BudgetMs, $"a parallel downsampling call took {elapsed} ms (budget {BudgetMs} ms)");
        }
    }

    /// <summary>
    ///     Every bin holds every window of every id; the last bin ends after To and holds only the windows that start
    ///     before To (one per id here).
    /// </summary>
    private static void AssertComplete(IReadOnlyList<(DateTime Bin, long Count)> rows, int bins, long ids, int windowsPerBin)
    {
        Assert.Equal(bins, rows.Count);
        Assert.All(rows.Take(rows.Count - 1), r => Assert.Equal(ids * windowsPerBin, r.Count));
        Assert.Equal(ids, rows[^1].Count);
    }

    private static async Task<IReadOnlyList<(DateTime Bin, long Count)>> DownsampleAsync(
        CrateDbStreamDataRepository repository, OctoObjectId rtId, DateTime from, DateTime to, int limit,
        IReadOnlyList<OctoObjectId> ids)
    {
        var result = await repository.ExecuteDownsamplingQueryAsync(rtId, StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([
                new AggregationColumn("Amount", AggregationFunction.Sum),
                new AggregationColumn("Amount", AggregationFunction.Count)
            ])
            .WithTimeRange(from, to)
            .WithLimit(limit)
            .WithRtIds(ids));
        return result.Rows
            .Select(r => (
                Bin: Convert.ToDateTime(r.Values["timestamp"], CultureInfo.InvariantCulture),
                Count: r.Values["amount_count"] is { } c ? Convert.ToInt64(c, CultureInfo.InvariantCulture) : 0L))
            .ToList();
    }

    private async Task<(OctoObjectId RtId, CrateDbStreamDataRepository Repository)> CreateSeededArchiveAsync(int entities)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, rtId.ToString());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
        [
            new ArchiveColumnDdl("Amount", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true),
            new ArchiveColumnDdl("Obis", new CrateColumnType.Primitive("TEXT"), false, true)
        ], 3, 0));

        // Server-side seed: entities x 15-minute windows over SeedDays, in slices so that no statement runs long.
        var start = SeedEnd.AddDays(-SeedDays);
        var windows = SeedDays * 96;
        var watch = Stopwatch.StartNew();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString + ";Pooling=false"))
        {
            await connection.OpenAsync();
            for (var e0 = 0; e0 < entities; e0 += 50)
            {
                var e1 = Math.Min(entities, e0 + 50) - 1;
                await using var command = new NpgsqlCommand(
                    $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"amount\", \"obis\") " +
                    $"SELECT '{start:O}'::TIMESTAMP + (CAST(w AS BIGINT) * 900000), " +
                    $"'{start:O}'::TIMESTAMP + ((CAST(w AS BIGINT) + 1) * 900000), " +
                    $"'67000000000000' || lpad(CAST(e AS TEXT), 10, '0'), 'Test/Meter', (e % 7) + w * 0.001, " +
                    $"CASE WHEN e % 2 = 0 THEN '1-1:1.9.0' ELSE '1-1:2.9.0' END " +
                    $"FROM generate_series({e0}, {e1}) AS g1(e), generate_series(0, {windows - 1}) AS g2(w)",
                    connection) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync();
            }
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");
        RunProgress.Report($"AB#8993 seeded {entities} entities x {windows} windows in {watch.Elapsed.TotalSeconds:F0} s");

        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(new ArchiveSnapshot(rtId, MeterType, CkArchiveStatus.Activated, null,
            [new CkArchiveColumnSpec("Amount", true, false), new CkArchiveColumnSpec("Obis", true, false)])
        {
            IsTimeRange = true,
            Period = Window
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store,
            A.Fake<IFormulaEngine>());
        return (rtId, repository);
    }
}
