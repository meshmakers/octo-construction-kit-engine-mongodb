using System.Globalization;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7360 at the repository boundary, without a database: local-day bin options produce a bin key per run of equal
/// bin width, need From and To but no Limit, are validated before any SQL, refuse a calendar rollup that cuts other days,
/// and empty bins read COUNT 0 (also per series). The numbers against a real CrateDB are in
/// <c>LocalDayDownsamplingIntegrationTests</c>.
/// </summary>
public class LocalDayDownsamplingQueryTests
{
    private static readonly OctoObjectId Raw = new("bb0000000000000000007360");
    private static readonly OctoObjectId GasDayRollup = new("bb0000000000000000007361");
    private static readonly OctoObjectId MidnightRollup = new("bb0000000000000000007362");
    private static readonly OctoObjectId MonthRollup = new("bb0000000000000000007363");
    private static readonly OctoObjectId UtcDayFixedRollup = new("bb0000000000000000007364");
    private static readonly OctoObjectId TimeRange = new("bb0000000000000000007365");
    private static readonly OctoObjectId SeriesA = new("bb00000000000000000073aa");
    private static readonly OctoObjectId SeriesB = new("bb00000000000000000073bb");
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));

    // Gas days 27 / 28 (23 h) / 29 March 2026, 06:00 Vienna.
    private static readonly DateTime From = new(2026, 3, 27, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 3, 30, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Bin0 = From;
    private static readonly DateTime Bin1 = new(2026, 3, 28, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Bin2 = new(2026, 3, 29, 4, 0, 0, DateTimeKind.Utc);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly IRollupArchiveRuntimeStore _rollups = A.Fake<IRollupArchiveRuntimeStore>();
    private readonly List<string> _sql = new();
    private List<DataPointDto> _rows = new();
    private long _straddlingWindows;

    public LocalDayDownsamplingQueryTests()
    {
        A.CallTo(() => _store.GetAsync(Raw)).Returns(new ArchiveSnapshot(Raw, Type, CkArchiveStatus.Activated, "raw",
            [new CkArchiveColumnSpec("Amount", true, false)]));
        RegisterRollup(GasDayRollup, BucketAlignment.CalendarDay, "Europe/Vienna", 21_600_000);
        RegisterRollup(MidnightRollup, BucketAlignment.CalendarDay, "Europe/Vienna", null);
        RegisterRollup(MonthRollup, BucketAlignment.CalendarMonth, "Europe/Vienna", null);
        RegisterRollup(UtcDayFixedRollup, BucketAlignment.FixedSize, null, null);
        A.CallTo(() => _store.GetAsync(TimeRange)).Returns(new ArchiveSnapshot(TimeRange, Type, CkArchiveStatus.Activated, "tr",
            [new CkArchiveColumnSpec("Amount", true, false)]) { IsTimeRange = true, Period = TimeSpan.FromHours(1) });
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _sql.Add(sql))
            .ReturnsLazily((string _, string sql) => Task.FromResult(sql.StartsWith(ProbePrefix, StringComparison.Ordinal)
                ? [new DataPointDto(new Dictionary<string, object?> { ["c"] = _straddlingWindows })]
                : _rows));
    }

    private const string ProbePrefix = "SELECT COUNT(*) AS \"c\"";

    private void RegisterRollup(OctoObjectId rtId, BucketAlignment alignment, string? zone, long? dayStartMs)
    {
        var specs = new[] { new CkRollupAggregationSpec("Amount", CkRollupFunction.Count, null) };
        A.CallTo(() => _store.GetAsync(rtId)).Returns(
            new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = TimeSpan.FromDays(1)
            });
        A.CallTo(() => _rollups.GetAsync(rtId)).Returns(
            new RollupArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, [new RollupSourceReference(Raw)],
                TimeSpan.FromDays(1), TimeSpan.Zero, null, specs, null)
            {
                BucketAlignment = alignment,
                ReferenceTimeZone = zone,
                DayStartOffsetMs = dayStartMs
            });
    }

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(), _db,
            A.Fake<IStreamDataDatabaseManagementClient>(), Options.Create(new StreamDataConfiguration { ConnectionString = "Host=x" }),
            "ld", _store, A.Fake<IFormulaEngine>(), _rollups);

    private static StreamDataDownsamplingQueryOptions GasDays(DateTime? from = null, DateTime? to = null) =>
        StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithAggregationColumns(
            [
                new AggregationColumn("Amount", AggregationFunction.Count),
                new AggregationColumn("Amount", AggregationFunction.Average)
            ])
            .WithTimeRange(from ?? From, to ?? To)
            .WithTimeZone("Europe/Vienna")
            .WithBinOrigin(TimeSpan.FromHours(6))
            .WithBinInterval(TimeSpan.FromDays(1));

    private static DataPointDto Row(DateTime bin, long count, double avg, OctoObjectId? series = null)
    {
        var attributes = new Dictionary<string, object?> { ["T"] = bin, ["Count_amount"] = count, ["Avg_amount"] = avg };
        if (series is { } rtId)
        {
            attributes["rtid"] = rtId.ToString();
        }

        return new DataPointDto(attributes) { Timestamp = bin, RtId = series };
    }

    private void AssertNoSql() => A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustNotHaveHappened();

    // ---- SQL ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GasDaysOverTheSpringChange_KeyRawRowsWithOneDateBinPerRun_AndStopAtTheEndOfTheLastBin()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays());

        var sql = Assert.Single(_sql); // no bucket-count probe: the axis does not depend on the data
        const string key =
            "CASE WHEN d.\"timestamp\" < '2026-03-28 05:00:00.000Z' THEN DATE_BIN('86400 seconds'::INTERVAL, d.\"timestamp\", '2026-03-27 05:00:00.000Z'::TIMESTAMP)" +
            " WHEN d.\"timestamp\" < '2026-03-29 04:00:00.000Z' THEN DATE_BIN('82800 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 05:00:00.000Z'::TIMESTAMP)" +
            " ELSE DATE_BIN('86400 seconds'::INTERVAL, d.\"timestamp\", '2026-03-29 04:00:00.000Z'::TIMESTAMP) END";
        Assert.StartsWith($"SELECT {key} AS \"T\"", sql);
        Assert.Contains($"GROUP BY {key}", sql);
        Assert.Contains($"ORDER BY {key} ASC", sql);
        Assert.Contains("AND d.\"timestamp\" <= '2026-03-30 04:00:00.000Z' AND d.\"timestamp\" < '2026-03-30 04:00:00.000Z'", sql);
        Assert.DoesNotContain("JOIN", sql);
        Assert.DoesNotContain("generate_series", sql);
    }

    [Fact]
    public async Task GasDaysWithoutADstChange_AreASingleDateBin()
    {
        var from = new DateTime(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc);

        await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays(from, from.AddDays(3)));

        Assert.StartsWith(
            "SELECT DATE_BIN('86400 seconds'::INTERVAL, d.\"timestamp\", '2026-06-01 04:00:00.000Z'::TIMESTAMP) AS \"T\"",
            Assert.Single(_sql));
    }

    [Fact]
    public async Task AMatchingGasDayRollup_IsContainedPerRun_WithTheBinEndOfItsRun()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(GasDayRollup, GasDays());

        var sql = Assert.Single(_sql);
        Assert.Contains(
            "AND d.\"window_end\" <= CASE WHEN d.\"window_start\" < '2026-03-28 05:00:00.000Z' THEN DATE_BIN('86400 seconds'::INTERVAL, d.\"window_start\", '2026-03-27 05:00:00.000Z'::TIMESTAMP) + '86400 seconds'::INTERVAL",
            sql);
        Assert.Contains("\"generation\" = 0", sql);
    }

    // ---- Limit, From, To (decision 2026-11-03: Limit not required for local-day bins) ---------------------------

    [Fact]
    public async Task LocalDayBins_NeedNoLimit_AndReturnOneRowPerDay()
    {
        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays().WithLimit(null));

        Assert.Equal([Bin0, Bin1, Bin2], result.Rows.Select(r => r.Timestamp));
    }

    [Fact]
    public async Task LocalDayBins_IgnoreALimit()
    {
        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays().WithLimit(500));

        Assert.Equal(3, result.Rows.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LocalDayBins_WithoutFromOrTo_AreRefused(bool withFrom, bool withTo)
    {
        var options = GasDays().WithTimeRange(withFrom ? From : null, withTo ? To : null);

        var ex = await Assert.ThrowsAsync<DownsamplingBinOptionsInvalidException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(Raw, options));

        Assert.Contains("From and To", ex.Message, StringComparison.Ordinal);
        AssertNoSql();
    }

    [Fact]
    public async Task AnEmptyRange_ReturnsNoBins_WithoutSql()
    {
        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays(To, From));

        Assert.Empty(result.Rows);
        AssertNoSql();
    }

    [Fact]
    public async Task FixedWidthBins_StillNeedALimit()
    {
        var options = StreamDataDownsamplingQueryOptions.Create().WithCkTypeId(Type)
            .WithAggregationColumns([new AggregationColumn("Amount", AggregationFunction.Count)])
            .WithTimeRange(From, To);

        // The unchanged message of the fixed-width path (the connection-level exception type of this layer).
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => NewSut().ExecuteDownsamplingQueryAsync(Raw, options));

        Assert.Contains("require From, To, and Limit", ex.Message, StringComparison.Ordinal);
    }

    // ---- validation (criterion 5) --------------------------------------------------------------------------------

    public static TheoryData<string, StreamDataDownsamplingQueryOptions> InvalidOptions => new()
    {
        { "TimeZone 'Mars/Olympus'", GasDays().WithTimeZone("Mars/Olympus") },
        { "BinOrigin 1.01:00:00", GasDays().WithBinOrigin(TimeSpan.FromHours(25)) },
        { "BinOrigin -01:00:00", GasDays().WithBinOrigin(TimeSpan.FromHours(-1)) },
        { "not a whole minute", GasDays().WithBinOrigin(new TimeSpan(6, 0, 30)) },
        { "not a whole number of days", GasDays().WithBinInterval(TimeSpan.FromHours(36)) },
        { "more than 10000 bins", GasDays(new DateTime(2000, 1, 1, 5, 0, 0, DateTimeKind.Utc), new DateTime(2030, 1, 1, 5, 0, 0, DateTimeKind.Utc)) },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task InvalidBinOptions_AreRefusedWithAMessageNamingTheOption_BeforeAnySql(
        string expected, StreamDataDownsamplingQueryOptions options)
    {
        var ex = await Assert.ThrowsAsync<DownsamplingBinOptionsInvalidException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(Raw, options));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        AssertNoSql();
    }

    [Theory]
    [InlineData("MidnightRollup", "CalendarDay rollup")]
    [InlineData("MonthRollup", "CalendarMonth rollup")]
    public async Task ACalendarRollupThatCutsOtherDays_IsRefused(string rollup, string expected)
    {
        var rtId = rollup == "MidnightRollup" ? MidnightRollup : MonthRollup;

        var ex = await Assert.ThrowsAsync<DownsamplingBinOptionsInvalidException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(rtId, GasDays()));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("starting at 06:00", ex.Message, StringComparison.Ordinal);
        AssertNoSql();
    }

    // ---- empty bins (criterion 3) --------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyGasDay_ReadsCount0_AndNullForOtherAggregations()
    {
        _rows = [Row(Bin0, 24, 1.0), Row(Bin2, 24, 1.0)];

        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays());

        var empty = result.Rows[1];
        Assert.Equal(Bin1, empty.Timestamp);
        Assert.Equal(0L, empty.Values["amount_count"]);
        Assert.Null(empty.Values["amount_avg"]);
        Assert.Equal(24L, result.Rows[0].Values["amount_count"]);
    }

    [Fact]
    public async Task FixedWidthBins_EmptyBinReadsCount0_Too()
    {
        // The deliberate behaviour change of AB#7360 applies to every downsampling query, not only to local days.
        var options = StreamDataDownsamplingQueryOptions.Create().WithCkTypeId(Type)
            .WithAggregationColumns(
            [
                new AggregationColumn("Amount", AggregationFunction.Count),
                new AggregationColumn("Amount", AggregationFunction.Average)
            ])
            .WithTimeRange(From, From.AddHours(3)).WithLimit(3);

        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, options);

        Assert.All(result.Rows, r =>
        {
            Assert.Equal(0L, r.Values["amount_count"]);
            Assert.Null(r.Values["amount_avg"]);
        });
    }

    [Fact]
    public async Task PerSeries_EverySeriesGetsEveryBin_AMissingDayOfOneSeriesReadsCount0WithItsRtId()
    {
        _rows = [Row(Bin0, 24, 1, SeriesA), Row(Bin0, 24, 1, SeriesB), Row(Bin1, 23, 1, SeriesA), Row(Bin2, 24, 1, SeriesB)];

        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays().WithGroupByColumns(["rtId"]));

        Assert.Equal(
        [
            (Bin0, SeriesA, 24L), (Bin0, SeriesB, 24L),
            (Bin1, SeriesA, 23L), (Bin1, SeriesB, 0L),
            (Bin2, SeriesA, 0L), (Bin2, SeriesB, 24L)
        ], result.Rows.Select(r => (r.Timestamp, r.RtId, Convert.ToInt64(r.Values["amount_count"]))));
        Assert.Null(result.Rows.Single(r => r.Timestamp == Bin1 && r.RtId == SeriesB).Values["amount_avg"]);
    }

    [Fact]
    public async Task PerSeries_WithoutAnyData_GivesOneEmptyRowPerBin()
    {
        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays().WithGroupByColumns(["rtId"]));

        Assert.Equal([Bin0, Bin1, Bin2], result.Rows.Select(r => r.Timestamp));
        Assert.All(result.Rows, r => Assert.Equal(0L, r.Values["amount_count"]));
    }

    // ---- whole local days at both ends (PR #226 review, point 1) --------------------------------------------------

    [Fact]
    public async Task AFromInsideAGasDay_ReadsTheWholeGasDay_FromItsStart()
    {
        // Reviewer's example: From 2026-03-27T00:00Z lies in the gas day that starts 2026-03-26T05:00Z.
        var from = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc);
        var gasDay26 = new DateTime(2026, 3, 26, 5, 0, 0, DateTimeKind.Utc);

        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays(from, To));

        var sql = Assert.Single(_sql);
        Assert.Contains("AND d.\"timestamp\" >= '2026-03-26 05:00:00.000Z'", sql);
        Assert.DoesNotContain("2026-03-27 00:00:00", sql);
        Assert.Equal([gasDay26, Bin0, Bin1, Bin2], result.Rows.Select(r => r.Timestamp));
    }

    [Fact]
    public async Task AToInsideAGasDay_ReadsTheWholeLastGasDay_UpToItsEnd()
    {
        var to = new DateTime(2026, 3, 29, 10, 0, 0, DateTimeKind.Utc);

        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays(From, to));

        var sql = Assert.Single(_sql);
        Assert.Contains("AND d.\"timestamp\" < '2026-03-30 04:00:00.000Z'", sql);
        Assert.DoesNotContain("2026-03-29 10:00:00", sql);
        Assert.Equal([Bin0, Bin1, Bin2], result.Rows.Select(r => r.Timestamp));
    }

    [Fact]
    public async Task AWindowedSource_ReadsTheWholeFirstAndLastGasDay_Too()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(GasDayRollup,
            GasDays(new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 29, 10, 0, 0, DateTimeKind.Utc)));

        var sql = Assert.Single(_sql);
        Assert.Contains("AND d.\"window_start\" < '2026-03-30 04:00:00.000Z' AND d.\"window_end\" > '2026-03-26 05:00:00.000Z'", sql);
    }

    // ---- windows dropped by the §7 containment (PR #226 review, point 2b) -----------------------------------------

    [Fact]
    public async Task ATimeRangeArchive_IsProbedForWindowsStraddlingALocalDay_WithTheBinEndOfTheDownsamplingSql()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, GasDays());

        Assert.Equal(2, _sql.Count);
        var probe = _sql[0];
        Assert.StartsWith(ProbePrefix + " FROM", probe);
        Assert.Contains(
            "AND d.\"window_end\" > CASE WHEN d.\"window_start\" < '2026-03-28 05:00:00.000Z' THEN DATE_BIN('86400 seconds'::INTERVAL, d.\"window_start\", '2026-03-27 05:00:00.000Z'::TIMESTAMP) + '86400 seconds'::INTERVAL",
            probe);
        Assert.Contains("AND d.\"window_start\" < '2026-03-30 04:00:00.000Z' AND d.\"window_end\" > '2026-03-27 05:00:00.000Z'", probe);
    }

    [Theory]
    [InlineData("TimeRange")]
    [InlineData("UtcDayFixedRollup")]
    public async Task AWindowedSourceWithWindowsStraddlingALocalDay_IsRefused_InsteadOfReadingCount0(string archive)
    {
        _straddlingWindows = 3;
        var rtId = archive == "TimeRange" ? TimeRange : UtcDayFixedRollup;

        var ex = await Assert.ThrowsAsync<DownsamplingBinOptionsInvalidException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(rtId, GasDays()));

        Assert.Contains("3 source window(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("straddle a local-day boundary", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith(ProbePrefix, Assert.Single(_sql)); // only the probe ran
    }

    [Fact]
    public async Task AWindowedSourceWithoutStraddlingWindows_IsAnswered()
    {
        _rows = [Row(Bin0, 24, 1.0)];

        var result = await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, GasDays());

        Assert.Equal([24L, 0L, 0L], result.Rows.Select(r => Convert.ToInt64(r.Values["amount_count"], CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ARawArchive_AndAMatchingGasDayRollup_NeedNoProbe()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays());
        await NewSut().ExecuteDownsamplingQueryAsync(GasDayRollup, GasDays());

        Assert.Equal(2, _sql.Count);
        Assert.DoesNotContain(_sql, sql => sql.StartsWith(ProbePrefix, StringComparison.Ordinal));
    }

    // ---- bins x series cap (PR #226 review, point 3) --------------------------------------------------------------

    private static List<DataPointDto> SeriesRows(int count) => Enumerable.Range(1, count)
        .Select(i => Row(Bin0, 1, 1.0, new OctoObjectId(i.ToString("x24", CultureInfo.InvariantCulture))))
        .ToList();

    [Fact]
    public async Task PerSeries_MoreRowsThanTheCap_IsRefused_NamingBinsSeriesAndTheCap()
    {
        _rows = SeriesRows(33_334); // 3 bins x 33,334 series = 100,002 rows

        var ex = await Assert.ThrowsAsync<DownsamplingBinOptionsInvalidException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays().WithGroupByColumns(["rtId"])));

        Assert.Contains("3 bins x 33334 series = 100002 rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cap of 100000 rows", ex.Message, StringComparison.Ordinal);
        Assert.Equal(100_000, CrateDbStreamDataRepository.MaxLocalDaySeriesRows);
    }

    [Fact]
    public async Task PerSeries_AtTheCap_IsAnswered()
    {
        _rows = SeriesRows(33_333); // 99,999 rows

        var result = await NewSut().ExecuteDownsamplingQueryAsync(Raw, GasDays().WithGroupByColumns(["rtId"]));

        Assert.Equal(99_999, result.Rows.Count);
    }
}
