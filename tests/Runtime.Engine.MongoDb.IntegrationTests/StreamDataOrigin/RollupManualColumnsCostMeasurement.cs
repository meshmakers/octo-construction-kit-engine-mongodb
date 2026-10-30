using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#7727: what the manual columns add to the aggregation of a recompute. Runs only when <c>OCTO_ROLLUP_COST_DAYS</c>
///     is set: it fills a time-range table with 1000 meters of 15-minute rows for that many days and times the daily
///     aggregation SELECT of a recompute with and without the two platform expressions, alternating, five rounds. The
///     same pair of SELECTs is what to run read-only against a large rollup of a real tenant (see
///     docs/streamdata-archive-concept.md, "Rollups report their manual values").
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupManualColumnsCostMeasurement(CrateStagingCopyFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureTheAggregationWithAndWithoutTheManualColumns()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("OCTO_ROLLUP_COST_DAYS"), out var days))
        {
            Assert.Skip("Set OCTO_ROLLUP_COST_DAYS to measure.");
        }

        var tenant = CrateStagingCopyFixture.NewTenantId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString());
        var day = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
            [new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)], 1, 0));
        const int meters = 1000;
        var start = new DateTimeOffset(day).ToUnixTimeMilliseconds();
        await fixture.SqlAsync(
            $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value\", \"row_origin\", \"manual_written_at\") " +
            $"SELECT ({start} + w * 900000)::timestamp, ({start} + (w + 1) * 900000)::timestamp, 'meter' || m, 'Test/Meter', FALSE, 1.0, " +
            "CASE WHEN (w + m) % 500 = 0 THEN 'manual' ELSE 'automated' END, " +
            $"CASE WHEN (w + m) % 500 = 0 THEN {start} ELSE NULL END " +
            $"FROM generate_series(0, {days * 96 - 1}) AS t1(w) CROSS JOIN generate_series(1, {meters}) AS t2(m)");
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var rows = await fixture.CountAsync($"SELECT COUNT(*) FROM {table}");

        const string Baseline = "\"rtid\", SUM(\"value\") AS \"value_sum\", COALESCE(MAX(\"was_updated\"), FALSE) AS \"was_updated\"";
        const string Manual = ", COUNT(CASE WHEN \"row_origin\" = 'manual' THEN 1 END) AS \"manual_count\", MAX(\"manual_written_at\") AS \"last_manual_at\"";

        var without = new List<double>();
        var with = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            foreach (var (columns, times) in new[] { (Baseline, without), (Baseline + Manual, with) })
            {
                var stopwatch = Stopwatch.StartNew();
                for (var d = 0; d < days; d++)
                {
                    var from = day.AddDays(d).ToString("O", CultureInfo.InvariantCulture);
                    var to = day.AddDays(d + 1).ToString("O", CultureInfo.InvariantCulture);
                    await fixture.QueryAsync($"SELECT {columns} FROM {table} WHERE \"window_start\" >= '{from}'::timestamp " +
                                             $"AND \"window_end\" <= '{to}'::timestamp GROUP BY \"rtid\"");
                }

                times.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        var report = string.Create(CultureInfo.InvariantCulture,
            $"AB#7727 cost: {rows} rows, {days} daily buckets, 1 shard: without {without.Order().ElementAt(2):F0} ms, with {with.Order().ElementAt(2):F0} ms (median of 5).");
        output.WriteLine(report);
        TestContext.Current.SendDiagnosticMessage(report);
    }
}
