using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#7359 against a real CrateDB: a gas-day rollup (CalendarDay, Europe/Vienna, day start 06:00) holds exactly one row
///     per gas day of 23, 24 or 25 hours — whether the row was written by the live orchestrator, a recompute, a repair or a
///     backfill — and a downsampling query over it returns those rows' <c>window_start</c> instants.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class GasDayRollupIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    private static readonly long SixHoursMs = (long)TimeSpan.FromHours(6).TotalMilliseconds;
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Sensor"));

    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    // Gas day 2026-03-28 is 23 h (spring change on the 29th), 2026-10-24 is 25 h (autumn change on the 25th).
    public static TheoryData<int, int, int, int, int, int, int[]> Ranges => new()
    {
        { 2026, 3, 27, 2026, 3, 30, new[] { 24, 23, 24 } },
        { 2026, 10, 23, 2026, 10, 26, new[] { 24, 25, 24 } },
        { 2026, 6, 30, 2026, 7, 2, new[] { 24, 24 } },
    };

    /// <summary>The UTC instant a gas day starting on the given Vienna date begins: 06:00 local.</summary>
    private static DateTime GasDayStart(int y, int m, int d) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, 6, 0, 0, DateTimeKind.Unspecified), Vienna);

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private sealed class Rig
    {
        public required CrateStagingCopyFixture Fixture { get; init; }
        public required string Tenant { get; init; }
        public required OctoObjectId Raw { get; init; }
        public required OctoObjectId Rollup { get; init; }
        public required ArchiveSnapshot RawSnapshot { get; init; }
        public required IArchiveRuntimeStore Archives { get; init; }
        public required InMemoryRollupStore Store { get; init; }
        public required CrateDbStreamDataRepository Repository { get; init; }
        public required DateTime RangeStart { get; init; }
        public required DateTime RangeEnd { get; init; }
        public string Live => TenantSchema.QualifiedArchiveTable(Tenant, Rollup.ToString());
        public string GenMap => GenerationMapSqlBuilder.GenMapTable(Tenant, Rollup.ToString());
        public RollupArchiveSnapshot Snapshot => Store.GetAsync(Rollup).Result!;

        /// <summary>The live pass: the engine's orchestrator ticks the rollup from its watermark to the end of the data.</summary>
        public async Task RunLiveAsync()
        {
            var orchestrator = new RollupOrchestrator(
                Tenant, Archives, Store, Repository, A.Fake<IArchiveAuditTrail>(), NullLogger<RollupOrchestrator>.Instance,
                1000, () => Utc(2027, 1, 1), false);
            await orchestrator.ProcessRollupAsync(Rollup, Ct);
            await Fixture.SqlAsync($"REFRESH TABLE {Live}");
        }

        /// <summary>The rows a reader sees: the active generation of every window.</summary>
        public async Task<List<(DateTime Start, DateTime End, long Count)>> ActiveRowsAsync()
        {
            await Fixture.SqlAsync($"REFRESH TABLE {Live}");
            await Fixture.SqlAsync($"REFRESH TABLE {GenMap}");
            var ranges = (await Fixture.QueryAsync($"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {GenMap}"))
                .Select(r => new GenerationRange(Convert.ToInt64(r["range_start"]), Convert.ToInt64(r["range_end"]),
                    Convert.ToString(r["rtid_scope"]) ?? string.Empty, Convert.ToInt64(r["generation"])))
                .ToList();
            var rows = await Fixture.QueryAsync(
                $"SELECT \"window_start\", \"window_end\", \"value_count\" FROM {Live} WHERE {GenerationFilterSql.Render(ranges)} ORDER BY 1");
            return rows.Select(r => (
                DateTime.SpecifyKind(Convert.ToDateTime(r["window_start"]).ToUniversalTime(), DateTimeKind.Utc),
                DateTime.SpecifyKind(Convert.ToDateTime(r["window_end"]).ToUniversalTime(), DateTimeKind.Utc),
                Convert.ToInt64(r["value_count"]))).ToList();
        }

        public async Task SqlAsync(string sql)
        {
            await Fixture.SqlAsync(sql);
            await Fixture.SqlAsync($"REFRESH TABLE {Live}");
        }
    }

    /// <param name="rangeStart">First gas-day start; also the initial watermark.</param>
    /// <param name="rangeEnd">End of the data (a gas-day boundary).</param>
    /// <param name="rawLeadHours">Hours after the range start the hourly raw rows begin (a partial first gas day).</param>
    private async Task<Rig> CreateRigAsync(DateTime rangeStart, DateTime rangeEnd, int rawLeadHours = 0)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var raw = OctoObjectId.GenerateNewId();
        var rollup = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, raw);
        var rawTable = TenantSchema.QualifiedArchiveTable(tenant, raw.ToString());
        var first = rangeStart.AddHours(rawLeadHours);
        var hours = (int)(rangeEnd - first).TotalHours;
        await fixture.SqlAsync(
            $"INSERT INTO {rawTable} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
            $"SELECT 'rt1', CAST({Ms(first)} + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'Test/Sensor-1', 1.0 " +
            $"FROM generate_series(0, {hours - 1})");
        await fixture.SqlAsync($"REFRESH TABLE {rawTable}");

        var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Count, null) };
        var snapshot = new RollupArchiveSnapshot(rollup, Type, CkArchiveStatus.Activated, "gasday", [new RollupSourceReference(raw)],
            TimeSpan.FromDays(1), TimeSpan.Zero, rangeStart, specs, null)
        {
            BucketAlignment = BucketAlignment.CalendarDay,
            ReferenceTimeZone = "Europe/Vienna",
            DayStartOffsetMs = SixHoursMs
        };
        var store = new InMemoryRollupStore(snapshot);
        var rawSnapshot = new ArchiveSnapshot(raw, Type, CkArchiveStatus.Activated, "raw", [new CkArchiveColumnSpec("Value", true, false)]);
        var rollupArchive = new ArchiveSnapshot(rollup, Type, CkArchiveStatus.Activated, "gasday", RollupColumnGenerator.Generate(specs))
        {
            RollupAggregations = specs,
            Period = TimeSpan.FromDays(1)
        };
        var archives = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archives.GetAsync(raw)).Returns(rawSnapshot);
        A.CallTo(() => archives.GetAsync(rollup)).Returns(rollupArchive);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, archives, A.Fake<IFormulaEngine>(),
            store, recomputeStateStore: null, archiveAuditTrail: null, null, new RecomputeProcessState());
        await repository.EnsureArchiveCreatedAsync(rollupArchive);
        return new Rig
        {
            Fixture = fixture, Tenant = tenant, Raw = raw, Rollup = rollup, RawSnapshot = rawSnapshot, Archives = archives, Store = store,
            Repository = repository, RangeStart = rangeStart, RangeEnd = rangeEnd
        };
    }

    private static void AssertGasDays(IReadOnlyList<(DateTime Start, DateTime End, long Count)> rows, int[] expectedHours, DateTime firstStart)
    {
        Assert.Equal(expectedHours.Length, rows.Count);
        var expectedStart = firstStart;
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(expectedStart, rows[i].Start);
            Assert.Equal(expectedHours[i], (rows[i].End - rows[i].Start).TotalHours);
            Assert.Equal(expectedHours[i], rows[i].Count); // hourly raw data: one row per hour of the gas day
            Assert.Equal(new TimeSpan(6, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(rows[i].Start, Vienna).TimeOfDay);
            Assert.Equal(new TimeSpan(6, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(rows[i].End, Vienna).TimeOfDay);
            expectedStart = rows[i].End;
        }
    }

    // ---- live pass (criterion 2) -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Ranges))]
    public async Task Live_WritesOneRowPerGasDay_With23_24_25HourBuckets(
        int y1, int m1, int d1, int y2, int m2, int d2, int[] expectedHours)
    {
        var from = GasDayStart(y1, m1, d1);
        var rig = await CreateRigAsync(from, GasDayStart(y2, m2, d2));

        await rig.RunLiveAsync();

        AssertGasDays(await rig.ActiveRowsAsync(), expectedHours, from);
    }

    [Theory]
    [InlineData(2026, 3, 28, 23)]
    [InlineData(2026, 10, 24, 25)]
    [InlineData(2026, 7, 1, 24)]
    public async Task Live_TheGasDayStartingOnTheDate_HasTheExpectedHours(int y, int m, int d, int hours)
    {
        var start = GasDayStart(y, m, d);
        var rig = await CreateRigAsync(start, GasDayStart(y, m, d).AddHours(hours));

        await rig.RunLiveAsync();

        var row = Assert.Single(await rig.ActiveRowsAsync());
        Assert.Equal((start, start.AddHours(hours), (long)hours), row);
        Assert.Equal(new TimeSpan(6, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(row.Start, Vienna).TimeOfDay);
    }

    // ---- recompute (criterion 3) -----------------------------------------------------------------------------

    [Fact]
    public async Task Recompute_OverARangeWithADstChange_ProducesTheRowsOfTheLivePass()
    {
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30));
        await rig.RunLiveAsync();
        var reference = await rig.ActiveRowsAsync();
        AssertGasDays(reference, [24, 23, 24], rig.RangeStart);

        await rig.Repository.ExecuteAsync(rig.RawSnapshot, rig.Snapshot, rig.RangeStart, rig.RangeEnd, null, Ct);

        Assert.Equal(reference, await rig.ActiveRowsAsync());
    }

    [Fact]
    public async Task Recompute_OfTheShortGasDayOnly_KeepsItsBoundariesAnd23Hours()
    {
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30));
        await rig.RunLiveAsync();
        var reference = await rig.ActiveRowsAsync();

        await rig.Repository.ExecuteAsync(rig.RawSnapshot, rig.Snapshot, GasDayStart(2026, 3, 28), GasDayStart(2026, 3, 29), null, Ct);

        Assert.Equal(reference, await rig.ActiveRowsAsync());
    }

    // ---- repair (criterion 3) --------------------------------------------------------------------------------

    [Fact]
    public async Task Repair_RewindsTheWatermarkToAGasDayBoundary_AndTheForwardPassRestoresTheLiveRows()
    {
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30));
        await rig.RunLiveAsync();
        var reference = await rig.ActiveRowsAsync();
        var cut = GasDayStart(2026, 3, 28);
        // the restored rollup ends after the first gas day; its watermark is ahead of the rows
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"window_start\" >= {Ms(cut)}");

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.Equal(RollupRepairKind.RewindWatermark, Assert.Single(report.Actions).Kind);
        Assert.Equal((rig.Rollup, cut, true), rig.Store.WatermarkWrites.Single(w => w.AllowRewind));
        Assert.True(report.IsOk);
        await rig.RunLiveAsync();
        Assert.Equal(reference, await rig.ActiveRowsAsync());
    }

    [Fact]
    public async Task Repair_OfAPointerWithoutRows_RecomputesTheGasDays()
    {
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30));
        await rig.RunLiveAsync();
        var reference = await rig.ActiveRowsAsync();
        await rig.Repository.ExecuteAsync(rig.RawSnapshot, rig.Snapshot, rig.RangeStart, rig.RangeEnd, null, Ct); // generation 1
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1"); // the restore lost the rows, the pointer stayed

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        Assert.Contains(report.Actions, a => a is { Kind: RollupRepairKind.RecomputePointerRange, Status: RollupRepairStatus.Done });
        Assert.Equal(reference, await rig.ActiveRowsAsync());
    }

    // ---- backfill (criterion 3) ------------------------------------------------------------------------------

    [Fact]
    public async Task Backfill_OfARollupWithoutRows_StartsAtTheGasDayOfTheEarliestSourceRow_AndEqualsTheLivePass()
    {
        // the raw data starts 3 h into the first gas day
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30), rawLeadHours: 3);
        await rig.RunLiveAsync();
        var reference = await rig.ActiveRowsAsync();
        Assert.Equal(21, reference[0].Count); // the partial first day
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"window_start\" > 0"); // no rollup row at all, the watermark is ahead

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.Equal(RollupRepairKind.RewindWatermark, Assert.Single(report.Actions).Kind);
        // the earliest source row lies in the gas day that began 06:00 Vienna (05:00 UTC), not at a midnight
        Assert.Equal((rig.Rollup, rig.RangeStart, true), rig.Store.WatermarkWrites.Single(w => w.AllowRewind));
        await rig.RunLiveAsync();
        Assert.Equal(reference, await rig.ActiveRowsAsync());
    }

    // ---- downsampling over the rollup (criterion 4) ----------------------------------------------------------

    [Fact]
    public async Task Downsampling_OverTheOffsetRollup_ReturnsOneBinPerGasDay_AtTheStoredWindowStarts()
    {
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30));
        await rig.RunLiveAsync();
        var stored = await rig.ActiveRowsAsync();
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithAggregationColumns([new AggregationColumn("Value", AggregationFunction.Count)])
            .WithTimeRange(rig.RangeStart, rig.RangeEnd)
            .WithLimit(3);

        var result = await rig.Repository.ExecuteDownsamplingQueryAsync(rig.Rollup, options);

        var ordered = result.Rows.OrderBy(r => Convert.ToDateTime(r.Values["timestamp"])).ToList();
        Assert.Equal(stored.Select(s => s.Start),
            ordered.Select(r => DateTime.SpecifyKind(Convert.ToDateTime(r.Values["timestamp"]).ToUniversalTime(), DateTimeKind.Utc)));
        Assert.Equal([24L, 23L, 24L], ordered.Select(r => Convert.ToInt64(r.Values["value_count"])).ToArray());
    }

    [Fact]
    public async Task LocalDayDownsampling_OverTheOffsetRollup_GivesTheStoredGasDays_AndAnEmptyGasDayReadsCount0()
    {
        // AB#7360 criterion 3 for a rollup source: gas-day bins (Europe/Vienna, 06:00, 1 day, no Limit) over the gas-day
        // rollup are its stored windows; a gas day without a stored row is a bin with Count 0.
        var rig = await CreateRigAsync(GasDayStart(2026, 3, 27), GasDayStart(2026, 3, 30));
        await rig.RunLiveAsync();
        var stored = await rig.ActiveRowsAsync();
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"window_start\" = {Ms(GasDayStart(2026, 3, 28))}");
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithAggregationColumns([new AggregationColumn("Value", AggregationFunction.Count)])
            .WithTimeRange(rig.RangeStart, rig.RangeEnd)
            .WithTimeZone("Europe/Vienna")
            .WithBinOrigin(TimeSpan.FromHours(6))
            .WithBinInterval(TimeSpan.FromDays(1));

        var result = await rig.Repository.ExecuteDownsamplingQueryAsync(rig.Rollup, options);

        Assert.Equal(stored.Select(s => s.Start),
            result.Rows.Select(r => DateTime.SpecifyKind(Convert.ToDateTime(r.Values["timestamp"]).ToUniversalTime(), DateTimeKind.Utc)));
        Assert.Equal([24L, 0L, 24L], result.Rows.Select(r => Convert.ToInt64(r.Values["value_count"])).ToArray());
    }
}
