using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataPaging;

/// <summary>
///     AB#9110: what the deterministic ORDER BY costs a paged simple query. Runs only when <c>OCTO_PAGING_COST_DAYS</c>
///     is set: it fills a raw archive (3 shards, the platform default) with 1000 meters of 15-minute rows for that many
///     days and times the page SQL of the simple query without ORDER BY (before) and with the row-key ORDER BY (after),
///     alternating, five rounds, for the first page and a deep page, unfiltered and filtered to one day.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class StreamDataPagingOrderCostMeasurement(CrateStagingCopyFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureThePageQueryWithAndWithoutTheRowKeyOrder()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("OCTO_PAGING_COST_DAYS"), out var days))
        {
            Assert.Skip("Set OCTO_PAGING_COST_DAYS to measure.");
        }

        var tenant = CrateStagingCopyFixture.NewTenantId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString());
        var day = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(table,
            [new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)], 3, 0));
        const int meters = 1000;
        var start = new DateTimeOffset(day).ToUnixTimeMilliseconds();
        await fixture.SqlAsync(
            $"INSERT INTO {table} (\"timestamp\", \"rtid\", \"cktypeid\", \"value\") " +
            $"SELECT ({start} + w::bigint * 900000)::timestamp, 'meter' || m, 'Test/Meter', 1.0 " +
            $"FROM generate_series(0, {days * 96 - 1}) AS t1(w) CROSS JOIN generate_series(1, {meters}) AS t2(m)");
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var rows = await fixture.CountAsync($"SELECT COUNT(*) FROM {table}");

        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"AB#9110 page cost: {rows} rows, 3 shards, page 50, median of 5 (ms)") };
        foreach (var (label, from, to, rowsInRange) in new (string, DateTime?, DateTime?, long)[]
                 {
                     ("unfiltered", null, null, rows),
                     ("one day", day, day.AddDays(1).AddMilliseconds(-1), 96L * meters)
                 })
        {
            foreach (var (pageLabel, offset) in new[] { ("first page", 0L), ("deep page", Math.Max(0, rowsInRange - 100)) })
            {
                var before = Compile(table, from, to, (int)offset, ordered: false);
                var after = Compile(table, from, to, (int)offset, ordered: true);
                var beforeTimes = new List<double>();
                var afterTimes = new List<double>();
                for (var round = 0; round < 5; round++)
                {
                    foreach (var (sql, times) in new[] { (before, beforeTimes), (after, afterTimes) })
                    {
                        var stopwatch = Stopwatch.StartNew();
                        await fixture.QueryAsync(sql);
                        times.Add(stopwatch.Elapsed.TotalMilliseconds);
                    }
                }

                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  {label}, {pageLabel} (offset {offset}): before {beforeTimes.Order().ElementAt(2):F0}, after {afterTimes.Order().ElementAt(2):F0}"));
            }
        }

        var report = string.Join(Environment.NewLine, lines);
        output.WriteLine(report);
        TestContext.Current.SendDiagnosticMessage(report);
    }

    /// <summary>The page SQL of the simple query as <c>ExecutePaginatedQueryAsync</c> builds it, with or without the row-key order.</summary>
    private static string Compile(string table, DateTime? from, DateTime? to, int offset, bool ordered)
    {
        var q = new CrateQueryBuilder(table).IncludeDefaultVariables();
        q.WithCkTypeIdFilter(new RtCkId<CkTypeId>("Test", new CkTypeId("Meter")));
        q.AddVariable("value", "value");
        if (from is not null || to is not null)
        {
            q.WithTimeFilter(from, to);
        }

        if (ordered)
        {
            q.EnsureDeterministicOrder();
        }

        if (offset > 0)
        {
            q.WithOffset(offset);
        }

        q.WithLimit(50);
        return new CrateQueryCompiler().CompileQuery(q);
    }
}
