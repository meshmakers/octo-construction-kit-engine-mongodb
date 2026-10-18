using System.Globalization;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     Measurement, not a regression test: the disk a rollup occupies when the same range is recomputed several times within the sweep
///     grace period (each recompute keeps the previous versions until its own sweep is due). It runs only with
///     <c>OCTO_ROLLUP_GRACE_MEASURE=1</c> against the Testcontainers CrateDB of the collection and writes its table to the file named by
///     <c>OCTO_ROLLUP_GRACE_MEASURE_OUT</c> (default: the temp folder). Size of the data: <c>OCTO_ROLLUP_GRACE_ENTITIES</c> entities times
///     <c>OCTO_ROLLUP_GRACE_HOURS</c> hourly windows (defaults 2000 x 1000 = 2,000,000 rollup rows).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupGraceDiskMeasurementTests(CrateStagingCopyFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Measure_DiskOfOneTwoAndThreeRecomputesWithinTheGracePeriod()
    {
        if (Environment.GetEnvironmentVariable("OCTO_ROLLUP_GRACE_MEASURE") != "1")
        {
            Assert.Skip("Measurement: set OCTO_ROLLUP_GRACE_MEASURE=1 to run it.");
        }

        var entities = int.Parse(Environment.GetEnvironmentVariable("OCTO_ROLLUP_GRACE_ENTITIES") ?? "2000", CultureInfo.InvariantCulture);
        var hours = int.Parse(Environment.GetEnvironmentVariable("OCTO_ROLLUP_GRACE_HOURS") ?? "1000", CultureInfo.InvariantCulture);
        var report = new StringBuilder();
        void Note(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rawId = OctoObjectId.GenerateNewId();
        var rollupId = OctoObjectId.GenerateNewId();
        var type = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor"));
        var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) };
        var raw = new ArchiveSnapshot(rawId, type, CkArchiveStatus.Activated, "raw", new[] { new CkArchiveColumnSpec("Value", true, false) });
        var rollupArchive = new ArchiveSnapshot(rollupId, type, CkArchiveStatus.Activated, "hourly", RollupColumnGenerator.Generate(specs))
        {
            RollupAggregations = specs,
            Period = TimeSpan.FromHours(1)
        };
        var rollup = new RollupArchiveSnapshot(rollupId, type, CkArchiveStatus.Activated, "hourly", new[] { new RollupSourceReference(rawId) },
            TimeSpan.FromHours(1), TimeSpan.Zero, null, specs, null);
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rawId)).Returns(raw);
        A.CallTo(() => store.GetAsync(rollupId)).Returns(rollupArchive);

        // the source: entities x hours rows
        await fixture.CreateRawArchiveAsync(tenant, rawId);
        var rawTable = TenantSchema.QualifiedArchiveTable(tenant, rawId.ToString());
        const int chunk = 100;
        for (var first = 0; first < entities; first += chunk)
        {
            var last = Math.Min(first + chunk, entities) - 1;
            await fixture.SqlAsync(
                $"INSERT INTO {rawTable} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
                "SELECT 'rt' || CAST(e.x AS TEXT), CAST(1767225600000 + CAST(h.y AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'Test/Sensor-1', h.y * 1.5 + e.x " +
                $"FROM generate_series({first}, {last}) AS e(x), generate_series(0, {hours - 1}) AS h(y)");
        }

        await fixture.SqlAsync($"REFRESH TABLE {rawTable}");
        await fixture.NewRollupRepository(tenant, rollupId).EnsureArchiveCreatedAsync(rollupArchive);
        var live = TenantSchema.QualifiedArchiveTable(tenant, rollupId.ToString());
        var clock = new ManualClock(new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero));
        var grace = TimeSpan.FromHours(2);
        var state = new RecomputeProcessState();
        var executor = new CrateDbArchiveRecomputeExecutor(tenant, fixture.Client, fixture.Client, store, NullLogger.Instance,
            rollupArchiveStore: null, sliceTargetRows: 100_000, state, grace, clock);
        var from = CrateStagingCopyFixture.RollupOrigin;
        var to = from.AddHours(hours);

        async Task<(long Rows, double Mib)> SizeAsync(bool merged = false)
        {
            await fixture.SqlAsync($"REFRESH TABLE {live}");
            if (merged)
            {
                await fixture.SqlAsync($"OPTIMIZE TABLE {live} WITH (max_num_segments = 1)");
                await fixture.SqlAsync($"REFRESH TABLE {live}");
            }

            var rows = await fixture.CountAsync($"SELECT COUNT(*) FROM {live}");
            var bytes = await fixture.CountAsync(
                "SELECT COALESCE(SUM(size), 0) FROM sys.shards " +
                $"WHERE schema_name = '{TenantSchema.SchemaName(tenant)}' AND table_name = '{TenantSchema.ArchiveTableName(rollupId.ToString())}' AND \"primary\" = TRUE");
            return (rows, bytes / 1048576.0);
        }

        var shardProbe = await fixture.QueryAsync(
            $"SELECT schema_name, table_name, \"primary\", size, num_docs FROM sys.shards WHERE table_name = '{TenantSchema.ArchiveTableName(rollupId.ToString())}'");
        Note($"shard probe: {shardProbe.Count} shard row(s); first: {(shardProbe.Count > 0 ? string.Join(", ", shardProbe[0].Select(kv => $"{kv.Key}={kv.Value}")) : "none")}");
        Note($"Rollup measurement: {entities} entities x {hours} hourly windows = {(long)entities * hours:N0} windows per version; CrateDB container, 1 GiB heap.");

        // The baseline version, with no grace period (nothing is kept).
        var baselineExecutor = new CrateDbArchiveRecomputeExecutor(tenant, fixture.Client, fixture.Client, store, NullLogger.Instance,
            rollupArchiveStore: null, sliceTargetRows: 100_000, new RecomputeProcessState(), TimeSpan.Zero, clock);
        await baselineExecutor.ExecuteAsync(raw, rollup, from, to, null, Ct);
        var baseline = await SizeAsync(merged: true);
        Note($"baseline (1 version, swept, merged): {baseline.Rows:N0} rows, {baseline.Mib:N0} MiB");

        var results = new List<(string Step, long Rows, double Mib, double MergedMib)>();
        for (var n = 1; n <= 3; n++)
        {
            clock.Now += TimeSpan.FromMinutes(10);
            await executor.ExecuteAsync(raw, rollup, from, to, null, Ct);
            var plain = await SizeAsync();
            Note($"after recompute {n} within the grace period: {plain.Rows:N0} rows, {plain.Mib:N0} MiB ({plain.Mib / baseline.Mib:F2}x the baseline)");
            var settled = await SizeAsync(merged: true);
            Note($"  same, merged to one segment: {settled.Mib:N0} MiB ({settled.Mib / baseline.Mib:F2}x)");
            results.Add(($"recompute {n}", plain.Rows, plain.Mib, settled.Mib));
        }

        var beforeSweep = await SizeAsync(merged: true);
        Note($"after recompute 3, merged to one segment: {beforeSweep.Rows:N0} rows, {beforeSweep.Mib:N0} MiB ({beforeSweep.Mib / baseline.Mib:F2}x)");

        clock.Now += grace + TimeSpan.FromMinutes(1);
        var swept = await executor.SweepDueAsync(rollupId, Ct);
        var afterSweep = await SizeAsync();
        Note($"sweep of {swept} pending record(s): {afterSweep.Rows:N0} rows, {afterSweep.Mib:N0} MiB before merging");
        await Task.Delay(TimeSpan.FromSeconds(20), Ct);
        await SizeAsync(merged: true);
        await Task.Delay(TimeSpan.FromSeconds(20), Ct);
        var afterMerge = await SizeAsync(merged: true);
        Note($"after the sweep, merged: {afterMerge.Rows:N0} rows, {afterMerge.Mib:N0} MiB ({afterMerge.Mib / baseline.Mib:F2}x the baseline)");

        var path = Environment.GetEnvironmentVariable("OCTO_ROLLUP_GRACE_MEASURE_OUT")
                   ?? Path.Combine(Path.GetTempPath(), "rollup-grace-disk.txt");
        await File.WriteAllTextAsync(path, report.ToString(), Ct);

        // What the measurement is about, as plain facts: no pending version is dropped before its sweep, so the rows grow by one version per recompute.
        Assert.Equal(baseline.Rows * 4, results[2].Rows);
        Assert.Equal(baseline.Rows, afterMerge.Rows);
    }
}
