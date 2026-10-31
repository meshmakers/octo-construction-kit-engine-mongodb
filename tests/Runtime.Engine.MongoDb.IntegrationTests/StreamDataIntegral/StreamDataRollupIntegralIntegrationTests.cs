using System.Globalization;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataIntegral;

/// <summary>
///     AB#7920: <c>Integral</c> over rollup archives against a real CrateDB. The ladder is a raw event archive, a
///     5-minute rollup that materialises a TimeWeightedAvg of <c>ActivePower</c> (and only a sample AVG of
///     <c>ImportPower</c>), and an hourly rollup over the 5-minute one — written, aggregated and read through the
///     production code (<see cref="CrateDbStreamDataRepository.AggregateBucketAsync" /> and the query paths).
///     Every rollup result is checked against the raw-archive Integral of the same rows (AB#7918) and, where the rows
///     allow it, against the analytic value. Windows are bucket-aligned except in the edge-bucket test, which pins
///     the documented rule: a rollup query reads every bucket that overlaps the window, in full.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class StreamDataRollupIntegralIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const double Tolerance = 1e-9;
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private const string MeterTypeLiteral = "Test/Meter";

    private static readonly DateTime From = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(2);

    private static readonly CkRollupAggregationSpec[] Specs =
    [
        new("ActivePower", CkRollupFunction.TimeWeightedAvg, null),
        new("ImportPower", CkRollupFunction.Avg, null)
    ];

    /// <summary>
    ///     The hourly rung chains the 5-minute pairs in the physical-column style: SUM of the stored integral and
    ///     duration (TWA) and of the stored sum and count (AVG).
    /// </summary>
    private static readonly CkRollupAggregationSpec[] CascadeSpecs =
    [
        new("activepower_twavg_integral", CkRollupFunction.Sum, "activepower_twavg_integral"),
        new("activepower_twavg_duration", CkRollupFunction.Sum, "activepower_twavg_duration"),
        new("importpower_avg_sum", CkRollupFunction.Sum, "importpower_avg_sum"),
        new("importpower_avg_count", CkRollupFunction.Sum, "importpower_avg_count")
    ];

    private static readonly ArchiveColumnDdl[] RawColumns =
    [
        new("ActivePower", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true),
        new("ImportPower", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true)
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Row(string RtId, DateTime Timestamp, double ActivePower);

    private sealed record Rung(ArchiveSnapshot Archive, RollupArchiveSnapshot Rollup)
    {
        public OctoObjectId RtId => Archive.RtId;
    }

    private sealed record Ladder(string Tenant, ArchiveSnapshot Raw, Rung FiveMinutes, Rung Hourly, CrateDbStreamDataRepository Repository)
    {
        public string Table(OctoObjectId rtId) => TenantSchema.QualifiedArchiveTable(Tenant, rtId.ToString());
    }

    private static Rung NewRung(OctoObjectId sourceRtId, TimeSpan bucket, CkRollupAggregationSpec[] specs)
    {
        var rtId = OctoObjectId.GenerateNewId();
        return new Rung(
            new ArchiveSnapshot(rtId, MeterType, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = bucket
            },
            new RollupArchiveSnapshot(rtId, MeterType, CkArchiveStatus.Activated, null,
                [new RollupSourceReference(sourceRtId)], bucket, TimeSpan.FromMinutes(1), null, specs, null));
    }

    /// <summary>
    ///     Creates the ladder, writes <paramref name="rows" /> to the raw archive and aggregates the 5-minute buckets of
    ///     [<paramref name="from" />, <paramref name="to" />) and the hourly buckets over them.
    /// </summary>
    private async Task<Ladder> CreateLadderAsync(IReadOnlyCollection<Row> rows, DateTime from, DateTime to)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rawRtId = OctoObjectId.GenerateNewId();
        var raw = new ArchiveSnapshot(rawRtId, MeterType, CkArchiveStatus.Activated, "raw",
            RawColumns.Select(c => new CkArchiveColumnSpec(c.Path, true, false)).ToArray());
        var fiveMinutes = NewRung(rawRtId, TimeSpan.FromMinutes(5), Specs);
        var hourly = NewRung(fiveMinutes.RtId, TimeSpan.FromHours(1), CascadeSpecs);

        var archives = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archives.GetAsync(rawRtId)).Returns(raw);
        A.CallTo(() => archives.GetAsync(fiveMinutes.RtId)).Returns(fiveMinutes.Archive);
        A.CallTo(() => archives.GetAsync(hourly.RtId)).Returns(hourly.Archive);
        var rollups = A.Fake<IRollupArchiveRuntimeStore>();
        A.CallTo(() => rollups.GetAsync(fiveMinutes.RtId)).Returns(fiveMinutes.Rollup);
        A.CallTo(() => rollups.GetAsync(hourly.RtId)).Returns(hourly.Rollup);

        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, archives,
            A.Fake<IFormulaEngine>(), rollupArchiveStore: rollups);
        var ladder = new Ladder(tenant, raw, fiveMinutes, hourly, repository);

        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(ladder.Table(rawRtId), RawColumns, 2, 0));
        foreach (var rung in new[] { fiveMinutes, hourly })
        {
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(ladder.Table(rung.RtId),
                RollupColumnTypeResolver.Resolve(rung.Archive.Columns, rung.Rollup.Aggregations), 1, 0, includeGeneration: true));
            await fixture.SqlAsync(GenerationMapSqlBuilder.BuildCreateTable(GenerationMapSqlBuilder.GenMapTable(tenant, rung.RtId.ToString())));
        }

        await InsertRawAsync(ladder, rows);

        for (var start = from; start < to; start = start.AddMinutes(5))
        {
            await repository.AggregateBucketAsync(raw, fiveMinutes.Rollup, start, start.AddMinutes(5), Ct);
        }

        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(fiveMinutes.RtId)}");

        for (var start = from; start < to; start = start.AddHours(1))
        {
            await repository.AggregateBucketAsync(fiveMinutes.Archive, hourly.Rollup, start, start.AddHours(1), Ct);
        }

        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(hourly.RtId)}");
        return ladder;
    }

    private async Task InsertRawAsync(Ladder ladder, IEnumerable<Row> rows)
    {
        var table = ladder.Table(ladder.Raw.RtId);
        foreach (var chunk in rows.Chunk(500))
        {
            var sql = new StringBuilder(
                $"INSERT INTO {table} (\"rtid\", \"timestamp\", \"cktypeid\", \"activepower\", \"importpower\") VALUES ");
            sql.AppendJoin(", ", chunk.Select(r =>
                $"('{r.RtId}', '{r.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}', '{MeterTypeLiteral}', " +
                $"{Literal(r.ActivePower)}, {Literal(Math.Max(r.ActivePower, 0))})"));
            await fixture.SqlAsync(sql.ToString());
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");
    }

    private static string Literal(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static AggregationColumn IntegralOf(string path) => new(path, AggregationFunction.Integral);

    private static async Task<double?> IntegralAsync(Ladder ladder, OctoObjectId archiveRtId, DateTime from, DateTime to,
        IReadOnlyList<OctoObjectId>? rtIds = null, string path = "ActivePower")
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([IntegralOf(path)])
            .WithTimeRange(from, to)
            .WithRtIds(rtIds);
        var result = await ladder.Repository.ExecuteAggregationQueryAsync(archiveRtId, options);
        return Value(Assert.Single(result.Rows), $"{path.ToLowerInvariant()}_integral");
    }

    private static double? Value(StreamDataRow row, string key) =>
        row.Values[key] is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;

    private static readonly string OnChange = OctoObjectId.GenerateNewId().ToString();
    private static readonly string FiveSeconds = OctoObjectId.GenerateNewId().ToString();

    /// <summary>
    ///     Two meters over [10:00, 12:00): one written on change (opened by a carry row at 09:58), one every 5 s.
    ///     On change: 2 kW · 7 min + 3 · 13 + 0 · 31 + 4 · 22 + 1.5 · 27 + 2.5 · 20 = 231.5 kW·min (89 in the first
    ///     hour, 142.5 in the second); every 5 s: 1 kW · 120 min. Total (231.5 + 120) / 60 kWh.
    /// </summary>
    private static List<Row> TwoMeters()
    {
        var rows = new List<Row>
        {
            new(OnChange, From.AddMinutes(-2), 2.0),
            new(OnChange, From.AddMinutes(7), 3.0),
            new(OnChange, From.AddMinutes(20), 0.0),
            new(OnChange, From.AddMinutes(51), 4.0),
            new(OnChange, From.AddMinutes(73), 1.5),
            new(OnChange, From.AddMinutes(100), 2.5)
        };
        for (var t = From; t < To; t = t.AddSeconds(5))
        {
            rows.Add(new Row(FiveSeconds, t, 1.0));
        }

        return rows;
    }

    private const double OnChangeKWh = 231.5 / 60;
    private const double FiveSecondsKWh = 2.0;

    [Fact]
    public async Task FiveMinuteAndHourlyRollups_GiveTheRawIntegral_OverAnAlignedWindow()
    {
        var ladder = await CreateLadderAsync(TwoMeters(), From, To);

        var raw = await IntegralAsync(ladder, ladder.Raw.RtId, From, To);
        var fiveMinutes = await IntegralAsync(ladder, ladder.FiveMinutes.RtId, From, To);
        var hourly = await IntegralAsync(ladder, ladder.Hourly.RtId, From, To);

        Assert.Equal(OnChangeKWh + FiveSecondsKWh, raw!.Value, Tolerance);
        Assert.Equal(raw.Value, fiveMinutes!.Value, Tolerance);
        Assert.Equal(raw.Value, hourly!.Value, Tolerance); // rollup on rollup: the summed integral column
    }

    [Fact]
    public async Task RtIdScope_AndGroupedQuery_GiveEachMetersIntegral()
    {
        var ladder = await CreateLadderAsync(TwoMeters(), From, To);

        Assert.Equal(OnChangeKWh, (await IntegralAsync(ladder, ladder.Hourly.RtId, From, To, [OctoObjectId.Parse(OnChange)]))!.Value,
            Tolerance);

        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithGroupByColumns(["rtId"])
            .WithTimeRange(From, To);
        var result = await ladder.Repository.ExecuteGroupedAggregationQueryAsync(ladder.FiveMinutes.RtId, options);

        var byMeter = result.Rows.ToDictionary(r => r.Values["rtid"]!.ToString()!, r => Value(r, "activepower_integral")!.Value);
        Assert.Equal(2, byMeter.Count);
        Assert.Equal(OnChangeKWh, byMeter[OnChange], Tolerance);
        Assert.Equal(FiveSecondsKWh, byMeter[FiveSeconds], Tolerance);
    }

    [Fact]
    public async Task Downsampling_OnTheFiveMinuteRollup_GivesTheIntegralOfEachHour()
    {
        // Two bins over two hours: each bin is the raw integral of its hour (the daily-kWh chart of AB#7945).
        var ladder = await CreateLadderAsync(TwoMeters(), From, To);

        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To)
            .WithLimit(2);
        var result = await ladder.Repository.ExecuteDownsamplingQueryAsync(ladder.FiveMinutes.RtId, options);

        var bins = result.Rows
            .Where(r => Value(r, "activepower_integral") is not null)
            .OrderBy(r => Convert.ToDateTime(r.Values["timestamp"], CultureInfo.InvariantCulture))
            .Select(r => Value(r, "activepower_integral")!.Value)
            .ToList();
        Assert.Equal(2, bins.Count);
        Assert.Equal(89.0 / 60 + 1.0, bins[0], Tolerance);
        Assert.Equal(142.5 / 60 + 1.0, bins[1], Tolerance);
        Assert.Equal((await IntegralAsync(ladder, ladder.Raw.RtId, From, From.AddHours(1)))!.Value, bins[0], Tolerance);
        Assert.Equal((await IntegralAsync(ladder, ladder.Raw.RtId, From.AddHours(1), To))!.Value, bins[1], Tolerance);
    }

    [Fact]
    public async Task AnUnalignedWindow_ReadsEveryOverlappingBucketInFull()
    {
        // The documented edge-bucket rule (same as every rollup aggregation): [10:07, 11:00) overlaps the 5-minute
        // bucket [10:05, 10:10), which is read whole. The rollup therefore answers [10:05, 11:00), not the raw
        // [10:07, 11:00): on the on-change meter the 2 kW of 10:05-10:07 are counted (+4 kW·min).
        var ladder = await CreateLadderAsync(TwoMeters(), From, To);
        var onChange = new[] { OctoObjectId.Parse(OnChange) };
        var unaligned = From.AddMinutes(7);
        var bucketStart = From.AddMinutes(5);
        var end = From.AddHours(1);

        var rollup = (await IntegralAsync(ladder, ladder.FiveMinutes.RtId, unaligned, end, onChange))!.Value;
        var rollupWholeBuckets = (await IntegralAsync(ladder, ladder.FiveMinutes.RtId, bucketStart, end, onChange))!.Value;
        var rawUnaligned = (await IntegralAsync(ladder, ladder.Raw.RtId, unaligned, end, onChange))!.Value;

        Assert.Equal(rollupWholeBuckets, rollup, Tolerance);
        Assert.Equal((89.0 - 2 * 5) / 60, rollup, Tolerance);
        Assert.Equal((89.0 - 2 * 7) / 60, rawUnaligned, Tolerance);
    }

    [Fact]
    public async Task AWindowWithoutBuckets_IsNull_NotZero()
    {
        var ladder = await CreateLadderAsync(TwoMeters(), From, To);

        Assert.Null(await IntegralAsync(ladder, ladder.FiveMinutes.RtId, To.AddHours(1), To.AddHours(2)));
        Assert.Null(await IntegralAsync(ladder, ladder.Hourly.RtId, To.AddHours(1), To.AddHours(2)));
    }

    [Fact]
    public async Task AColumnWithOnlyASampleAverage_IsRefused_NamingTheMissingAggregation()
    {
        var ladder = await CreateLadderAsync(TwoMeters(), From, To);

        foreach (var rung in new[] { ladder.FiveMinutes, ladder.Hourly })
        {
            var ex = await Assert.ThrowsAsync<StreamDataException>(() =>
                IntegralAsync(ladder, rung.RtId, From, To, path: "ImportPower"));
            Assert.Contains("needs a TimeWeightedAvg aggregation of 'ImportPower'", ex.Message, StringComparison.Ordinal);
            Assert.Equal(rung.RtId, ex.ArchiveRtId);
        }
    }

    /// <summary>
    ///     The recorded 5-s grid rows of the AB#7918 meter check (<c>grid-5s-meter-check-ab7692.csv</c>, signed
    ///     ActivePower), integrated by the 5-minute rollup over the aligned window [10:35, 11:15) inside the recording:
    ///     the same value as the raw Integral of the same rows.
    /// </summary>
    [Fact]
    public async Task RecordedGridRows_RollupIntegral_EqualsTheRawIntegral()
    {
        var rt = OctoObjectId.GenerateNewId().ToString();
        var rows = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "testData", "integral", "grid-5s-meter-check-ab7692.csv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("timestamp", StringComparison.Ordinal))
            .Select(l => l.Split(','))
            .Select(p => new Row(rt,
                DateTime.Parse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                double.Parse(p[1], CultureInfo.InvariantCulture)))
            .ToList();
        var from = new DateTime(2026, 10, 30, 10, 35, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 30, 11, 15, 0, DateTimeKind.Utc);
        var ladder = await CreateLadderAsync(rows, from, to);

        var raw = (await IntegralAsync(ladder, ladder.Raw.RtId, from, to))!.Value;
        var rollup = (await IntegralAsync(ladder, ladder.FiveMinutes.RtId, from, to))!.Value;

        Assert.True(Math.Abs(raw) > 0.01, $"The window must carry energy to compare (raw {raw} kWh).");
        Assert.Equal(raw, rollup, Tolerance);
    }
}
