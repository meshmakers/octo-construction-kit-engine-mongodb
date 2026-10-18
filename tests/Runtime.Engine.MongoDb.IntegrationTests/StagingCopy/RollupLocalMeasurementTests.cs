using System.Diagnostics;
using System.Globalization;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#6968 measurements on a large rollup of the local kind CrateDB: the runtime of the validator, and the storage and
///     query cost of keeping both versions of a recomputed rollup during the sweep grace period. Runs only when
///     <c>OCTO_ROLLUP_MEASURE_SOURCE_TENANT</c> and <c>OCTO_ROLLUP_MEASURE_ROLLUP</c> name a rollup of a local tenant; the source
///     is only read (its rows are copied into a schema of the test's own), the figures go to the file named by
///     <c>OCTO_ROLLUP_MEASURE_REPORT</c>, and the test's tables and store objects are removed at the end.
/// </summary>
[Collection(LocalKindStagingCollection.Name)]
public class RollupLocalMeasurementTests(LocalKindStagingFixture fixture)
{
    private const string TenantVariable = "OCTO_ROLLUP_MEASURE_SOURCE_TENANT";
    private const string RollupVariable = "OCTO_ROLLUP_MEASURE_ROLLUP";
    private const string ReportVariable = "OCTO_ROLLUP_MEASURE_REPORT";
    private static CancellationToken Ct => CancellationToken.None;

    private sealed class Rig
    {
        public required string Tenant { get; init; }
        public required OctoObjectId Rollup { get; init; }
        public required CrateDbStreamDataRepository Repository { get; init; }
        public required RollupArchiveSnapshot Snapshot { get; init; }
        public string Live => TenantSchema.QualifiedArchiveTable(Tenant, Rollup.ToString());
        public string GenMap => GenerationMapSqlBuilder.GenMapTable(Tenant, Rollup.ToString());
    }

    private Rig NewRig(string tenant, OctoObjectId rollup, DateTime watermark)
    {
        var type = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor"));
        var specs = new[] { new CkRollupAggregationSpec("Amount.Value", CkRollupFunction.Sum, null) };
        var snapshot = new RollupArchiveSnapshot(rollup, type, CkArchiveStatus.Activated, "measure", [new RollupSourceReference(OctoObjectId.GenerateNewId())],
            TimeSpan.FromHours(1), TimeSpan.Zero, watermark, specs, null);
        var archives = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archives.GetAsync(rollup)).Returns(new ArchiveSnapshot(rollup, type, CkArchiveStatus.Activated, "measure", [])
        {
            RollupAggregations = specs,
            Period = TimeSpan.FromHours(1)
        });
        var rollups = A.Fake<IRollupArchiveRuntimeStore>();
        A.CallTo(() => rollups.EnumerateAsync()).ReturnsLazily(() => One(snapshot));
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(),
            fixture.Client, fixture.Client, fixture.Configuration, tenant, archives, A.Fake<IFormulaEngine>(), rollups);
        return new Rig { Tenant = tenant, Rollup = rollup, Repository = repository, Snapshot = snapshot };
    }

    private static async IAsyncEnumerable<RollupArchiveSnapshot> One(RollupArchiveSnapshot snapshot)
    {
        await Task.CompletedTask;
        yield return snapshot;
    }

    private async Task<double> TimedAsync(Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        return watch.Elapsed.TotalSeconds;
    }

    private async Task<long> SizeMibAsync(string schema, string table)
        => Convert.ToInt64(await fixture.ScalarAsync(
            $"SELECT COALESCE(SUM(size), 0) / 1048576 FROM sys.shards WHERE schema_name = '{schema}' AND table_name = '{table}' AND \"primary\""), CultureInfo.InvariantCulture);

    private async Task<double[]> QueryTimesAsync(string sql, int repeat = 3)
    {
        var times = new double[repeat];
        for (var i = 0; i < repeat; i++)
        {
            times[i] = await TimedAsync(() => fixture.QueryAsync(sql));
        }

        return times;
    }

    private static string Fmt(double[] seconds) => string.Join(" / ", seconds.Select(s => (s * 1000).ToString("0", CultureInfo.InvariantCulture))) + " ms";

    [Fact]
    public async Task Measurement_ValidatorAndSweepGracePeriodOnALargeLocalRollup()
    {
        Assert.SkipUnless(fixture.Available, $"The local kind infrastructure is not usable: {fixture.Reason}");
        var sourceTenant = Environment.GetEnvironmentVariable(TenantVariable);
        var rollupText = Environment.GetEnvironmentVariable(RollupVariable);
        Assert.SkipUnless(!string.IsNullOrEmpty(sourceTenant) && !string.IsNullOrEmpty(rollupText), $"{TenantVariable} / {RollupVariable} not set");
        var reportPath = Environment.GetEnvironmentVariable(ReportVariable);

        var rollup = OctoObjectId.Parse(rollupText!);
        var targetTenant = "rmeasure" + Guid.NewGuid().ToString("N")[..6];
        var prefix = $"agent-rollup-measure/{Guid.NewGuid():N}";
        var sourceSchema = TenantSchema.SchemaName(sourceTenant!);
        var targetSchema = TenantSchema.SchemaName(targetTenant);
        var tableName = TenantSchema.ArchiveTableName(rollup.ToString());
        var sourceTable = $"\"{sourceSchema}\".\"{tableName}\"";
        var report = new StringBuilder();
        report.AppendLine($"source '{sourceSchema}'.{tableName} (read only); target schema '{targetSchema}'; store prefix '{prefix}'");
        var version = await fixture.ScalarAsync("SELECT version['number'] FROM sys.nodes LIMIT 1");
        report.AppendLine($"CrateDB {version}");

        var rows = Convert.ToInt64(await fixture.ScalarAsync($"SELECT COUNT(*) FROM {sourceTable}"), CultureInfo.InvariantCulture);
        var first = Convert.ToInt64(await fixture.ScalarAsync($"SELECT MIN(window_start) FROM {sourceTable}"), CultureInfo.InvariantCulture);
        var maxEnd = (DateTime)(await fixture.ScalarAsync($"SELECT MAX(window_end) FROM {sourceTable}"))!;
        var rig = NewRig(targetTenant, rollup, DateTime.SpecifyKind(maxEnd, DateTimeKind.Utc));
        report.AppendLine($"rollup rows {rows}");

        try
        {
            // 1. the rollup in a schema of its own: native copy of the data table and the generation map
            var ddl = (string)(await fixture.ScalarAsync($"SHOW CREATE TABLE {sourceTable}"))!;
            await fixture.SqlAsync(ddl.Replace($"\"{sourceSchema}\".", $"\"{targetSchema}\".", StringComparison.Ordinal));
            await fixture.SqlAsync(GenerationMapSqlBuilder.BuildCreateTable(rig.GenMap));
            var source = NewRig(sourceTenant!, rollup, rig.Snapshot.LastAggregatedBucketEnd!.Value);
            var location = fixture.Store.Location(prefix);
            var options = new ArchiveCopyOptions();
            ArchiveCopyDumpResult dump = null!;
            var dumpSeconds = await TimedAsync(async () => dump = await source.Repository.DumpArchiveToStagingAsync(rollup, location, options, Ct));
            Assert.True(dump.IsSuccess);
            ArchiveCopyLoadResult load = null!;
            var loadSeconds = await TimedAsync(async () => load = await rig.Repository.LoadArchiveFromStagingAsync(rollup, dump.Plan, location, options, Ct));
            Assert.True(load.RowCountVerified);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"native copy of the rollup (data + generation map, {dump.Plan.Slices.Count} slices): dump {dumpSeconds:0.0} s ({dump.Attempts} attempt(s), map read -> last data slice {(dump.LastDataCopiedAtUtc - dump.MapReadAtUtc)?.TotalSeconds:0.0} s), load {loadSeconds:0.0} s, row count verified: {load.RowCountVerified}"));

            // 2. the validator on the consistent rollup (generations 1..n, one pointer entry each)
            RollupConsistencyReport baseline = null!;
            var baselineSeconds = await TimedAsync(async () => baseline = await rig.Repository.ValidateRollupsAsync(null, Ct));
            var entryCount = await fixture.ScalarAsync($"SELECT COUNT(*) FROM {rig.GenMap}");
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"validator, {rows} rows, {entryCount} pointer entries: {baselineSeconds:0.00} s, consistent: {baseline.IsOk}"));

            // 3. storage and reads with one version
            var sizeOne = await SizeMibAsync(targetSchema, tableName);
            var pointers = await ReadPointersAsync(rig);
            var predicate = GenerationFilterSql.Render(pointers);
            var monthFrom = first + 200L * 86_400_000;
            var queries = new (string Name, string Sql)[]
            {
                ("one month grouped by entity", $"SELECT rtid, SUM(amountvalue_sum) FROM {rig.Live} WHERE window_start >= {monthFrom} AND window_start < {monthFrom + 30L * 86_400_000} AND {{0}} GROUP BY rtid"),
                ("whole range by month", $"SELECT date_trunc('month', window_start) AS m, SUM(amountvalue_sum) FROM {rig.Live} WHERE {{0}} GROUP BY 1 ORDER BY 1"),
                ("one entity, one day", $"SELECT window_start, amountvalue_sum FROM {rig.Live} WHERE rtid = (SELECT MIN(rtid) FROM {rig.Live} WHERE window_start >= {monthFrom} AND window_start < {monthFrom + 3_600_000}) AND window_start >= {monthFrom} AND window_start < {monthFrom + 86_400_000} AND {{0}}"),
            };
            var timesOne = new List<(string, double[])>();
            foreach (var (name, sql) in queries)
            {
                timesOne.Add((name, await QueryTimesAsync(string.Format(CultureInfo.InvariantCulture, sql, predicate))));
            }

            // 4. a full recompute: the second version of every row under the next generation, one pointer entry over the whole range
            var nextGeneration = pointers.Max(p => p.Generation) + 1;
            var last = Convert.ToInt64(await fixture.ScalarAsync($"SELECT MAX(window_start) FROM {rig.Live}"), CultureInfo.InvariantCulture);
            var copySeconds = await TimedAsync(async () =>
            {
                for (var from = first; from <= last; from += 30L * 86_400_000)
                {
                    await fixture.SqlAsync(
                        $"INSERT INTO {rig.Live} (window_start, window_end, rtid, cktypeid, rtcreationdatetime, rtchangeddatetime, rtwellknownname, was_updated, generation, amountvalue_sum, dataquality_max) " +
                        $"SELECT window_start, window_end, rtid, cktypeid, rtcreationdatetime, rtchangeddatetime, rtwellknownname, was_updated, {nextGeneration}, amountvalue_sum, dataquality_max " +
                        $"FROM {rig.Live} WHERE window_start >= {from} AND window_start < {from + 30L * 86_400_000} AND generation < {nextGeneration}");
                }

                await fixture.SqlAsync(GenerationMapSqlBuilder.BuildUpsertPointer(rig.GenMap,
                    DateTimeOffset.FromUnixTimeMilliseconds(first).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(last + 1).UtcDateTime, "", nextGeneration));
                await fixture.SqlAsync($"REFRESH TABLE {rig.Live}");
                await fixture.SqlAsync($"REFRESH TABLE {rig.GenMap}");
            });
            var rowsBoth = Convert.ToInt64(await fixture.ScalarAsync($"SELECT COUNT(*) FROM {rig.Live}"), CultureInfo.InvariantCulture);
            var sizeBoth = await SizeMibAsync(targetSchema, tableName);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"second version written (generation {nextGeneration}, stand-in for a recompute of the whole range): {copySeconds:0} s, rows {rows} -> {rowsBoth}"));
            report.AppendLine($"storage of the table: {sizeOne} MiB with one version -> {sizeBoth} MiB with both ({(sizeBoth / (double)Math.Max(sizeOne, 1)).ToString("0.00", CultureInfo.InvariantCulture)}x)");

            pointers = await ReadPointersAsync(rig);
            predicate = GenerationFilterSql.Render(pointers);
            report.AppendLine("reads (three runs each; first / second / third):");
            for (var i = 0; i < queries.Length; i++)
            {
                var both = await QueryTimesAsync(string.Format(CultureInfo.InvariantCulture, queries[i].Sql, predicate));
                report.AppendLine($"  {queries[i].Name}: one version {Fmt(timesOne[i].Item2)}; both versions {Fmt(both)}");
            }

            // 5. the validator with both versions present (the second one outranks the first: nothing to report)
            RollupConsistencyReport both2 = null!;
            var bothSeconds = await TimedAsync(async () => both2 = await rig.Repository.ValidateRollupsAsync(null, Ct));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"validator with both versions ({rowsBoth} rows, {pointers.Count} pointer entries): {bothSeconds:0.00} s, consistent: {both2.IsOk}"));

            // 6. the deferred sweep, as the executor runs it after the grace period (a record that is due)
            var executor = new CrateDbArchiveRecomputeExecutor(targetTenant, fixture.Client, fixture.Client, A.Fake<IArchiveRuntimeStore>(),
                NullLogger.Instance, rollupArchiveStore: null, sliceTargetRows: 100_000, new RecomputeProcessState(), TimeSpan.FromHours(2), TimeProvider.System);
            var sweepTable = PendingSweepSqlBuilder.SweepTable(targetTenant, rollup.ToString());
            await fixture.SqlAsync(PendingSweepSqlBuilder.BuildCreateTable(sweepTable));
            await fixture.SqlAsync(PendingSweepSqlBuilder.BuildUpsert(sweepTable,
                new GenerationRange(first, last + 1, string.Empty, nextGeneration), DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds()));
            await fixture.SqlAsync($"REFRESH TABLE {sweepTable}");
            var swept = 0;
            var sweepSeconds = await TimedAsync(async () => swept = await executor.SweepDueAsync(rollup, Ct));
            await fixture.SqlAsync($"REFRESH TABLE {rig.Live}");
            var rowsAfter = Convert.ToInt64(await fixture.ScalarAsync($"SELECT COUNT(*) FROM {rig.Live}"), CultureInfo.InvariantCulture);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"deferred sweep of {rowsBoth - rowsAfter} superseded rows (records executed: {swept}): {sweepSeconds:0} s ({(rowsBoth - rowsAfter) / Math.Max(sweepSeconds, 0.001):0} rows/s); rows {rowsBoth} -> {rowsAfter}"));
        }
        finally
        {
            await fixture.SqlAsync($"DROP TABLE IF EXISTS \"{targetSchema}\".\"{tableName}\"");
            await fixture.SqlAsync($"DROP TABLE IF EXISTS \"{targetSchema}\".\"{tableName}__genmap\"");
            await fixture.SqlAsync($"DROP TABLE IF EXISTS \"{targetSchema}\".\"{tableName}__sweep\"");
            var deleted = await fixture.Store.DeletePrefixAsync(prefix, CancellationToken.None);
            report.AppendLine($"cleanup: dropped the tables in schema '{targetSchema}', deleted {deleted} objects under '{prefix}'");
            if (!string.IsNullOrEmpty(reportPath))
            {
                await File.WriteAllTextAsync(reportPath, report.ToString(), CancellationToken.None);
            }
        }
    }

    private async Task<IReadOnlyList<GenerationRange>> ReadPointersAsync(Rig rig)
    {
        await fixture.SqlAsync($"REFRESH TABLE {rig.GenMap}");
        return (await fixture.QueryAsync($"SELECT range_start, range_end, rtid_scope, generation FROM {rig.GenMap}"))
            .Select(r => new GenerationRange(Convert.ToInt64(r["range_start"], CultureInfo.InvariantCulture), Convert.ToInt64(r["range_end"], CultureInfo.InvariantCulture),
                Convert.ToString(r["rtid_scope"], CultureInfo.InvariantCulture) ?? string.Empty, Convert.ToInt64(r["generation"], CultureInfo.InvariantCulture))).ToList();
    }
}
