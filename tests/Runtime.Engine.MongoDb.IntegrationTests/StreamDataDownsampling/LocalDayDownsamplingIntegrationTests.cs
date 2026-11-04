using System.Diagnostics;
using System.Globalization;
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

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataDownsampling;

/// <summary>
///     AB#7360 against a real CrateDB: a downsampling query with <c>TimeZone</c>, <c>BinOrigin</c> and <c>BinInterval</c>
///     returns one bin per local day (23 / 24 / 25 h across DST changes) directly from raw and time-range data, per series
///     on the same axis, and an empty day reads Count 0. The rollup source is covered in <c>GasDayRollupIntegrationTests</c>.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class LocalDayDownsamplingIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly string TypeLiteral = Type.SemanticVersionedFullName;
    private static readonly OctoObjectId SeriesA = new("cc00000000000000000073aa");
    private static readonly OctoObjectId SeriesB = new("cc00000000000000000073bb");

    private sealed record Archive(OctoObjectId RtId, string Table, CrateDbStreamDataRepository Repository);

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    /// <summary>The UTC instant of a local Vienna wall-clock time.</summary>
    private static DateTime ViennaUtc(int y, int m, int d, int h = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Unspecified), Vienna);

    private CrateDbStreamDataRepository Repository(string tenant, OctoObjectId rtId, ArchiveSnapshot snapshot)
    {
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(snapshot);
        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(),
            fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>());
    }

    private async Task<Archive> CreateRawAsync()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, rtId);
        var snapshot = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "raw", [new CkArchiveColumnSpec("Value", true, false)]);
        return new Archive(rtId, TenantSchema.QualifiedArchiveTable(tenant, rtId.ToString()), Repository(tenant, rtId, snapshot));
    }

    /// <summary>One raw row an hour for <paramref name="series"/> in <c>[from, to)</c>, value 1.</summary>
    private async Task InsertHourlyAsync(Archive archive, OctoObjectId series, DateTime from, DateTime to)
    {
        var hours = (int)(to - from).TotalHours;
        await fixture.SqlAsync(
            $"INSERT INTO {archive.Table} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
            $"SELECT '{series}', CAST({Ms(from)} + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), '{TypeLiteral}', 1.0 " +
            $"FROM generate_series(0, {hours - 1})");
    }

    private async Task RefreshAsync(Archive archive) => await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");

    private static StreamDataDownsamplingQueryOptions LocalDays(string column, DateTime from, DateTime to, int originHour) =>
        StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithAggregationColumns(
            [
                new AggregationColumn(column, AggregationFunction.Count),
                new AggregationColumn(column, AggregationFunction.Sum)
            ])
            .WithTimeRange(from, to)
            .WithTimeZone("Europe/Vienna")
            .WithBinOrigin(TimeSpan.FromHours(originHour))
            .WithBinInterval(TimeSpan.FromDays(1));

    private sealed record Bin(DateTime Start, OctoObjectId? RtId, long Count, object? Sum);

    private static List<Bin> Bins(StreamDataQueryResult result, string column) => result.Rows
        .Select(r => new Bin(
            AsUtc(Convert.ToDateTime(r.Values["timestamp"], CultureInfo.InvariantCulture)),
            r.RtId,
            Convert.ToInt64(r.Values[$"{column}_count"], CultureInfo.InvariantCulture),
            r.Values[$"{column}_sum"]))
        .ToList();

    // ---- criterion 1: gas days from raw data ----------------------------------------------------------------------

    [Fact]
    public async Task GasDaysFromHourlyRawData_AcrossTheSpringChange_Are24_23_24Hours()
    {
        var archive = await CreateRawAsync();
        // data reaches a day beyond both ends: the bins must count only their own gas day
        await InsertHourlyAsync(archive, SeriesA, Utc(2026, 3, 26), Utc(2026, 3, 31));
        await RefreshAsync(archive);
        var from = new DateTime(2026, 3, 27, 5, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 3, 30, 4, 0, 0, DateTimeKind.Utc);

        var bins = Bins(await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId, LocalDays("Value", from, to, 6)), "value");

        Assert.Equal([from, Utc(2026, 3, 28, 5), Utc(2026, 3, 29, 4)], bins.Select(b => b.Start));
        Assert.All(bins, b => Assert.Equal(new TimeSpan(6, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(b.Start, Vienna).TimeOfDay));
        Assert.Equal([24L, 23L, 24L], bins.Select(b => b.Count));
    }

    [Fact]
    public async Task GasDayStarting24October_FromHourlyRawData_Has25Hours()
    {
        var archive = await CreateRawAsync();
        await InsertHourlyAsync(archive, SeriesA, Utc(2026, 10, 22), Utc(2026, 10, 28));
        await RefreshAsync(archive);

        var bins = Bins(await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId,
            LocalDays("Value", ViennaUtc(2026, 10, 23, 6), ViennaUtc(2026, 10, 26, 6), 6)), "value");

        Assert.Equal([ViennaUtc(2026, 10, 23, 6), ViennaUtc(2026, 10, 24, 6), ViennaUtc(2026, 10, 25, 6)], bins.Select(b => b.Start));
        Assert.Equal([24L, 25L, 24L], bins.Select(b => b.Count));
    }

    // ---- criteria 2 and 3: per series, empty days ------------------------------------------------------------------

    [Fact]
    public async Task PerSeries_EachSeriesGetsTheSameAxis_AnEmptyDayOfOneSeriesReadsCount0AndSumNull()
    {
        var archive = await CreateRawAsync();
        var from = ViennaUtc(2026, 3, 27, 6);
        var to = ViennaUtc(2026, 3, 30, 6);
        await InsertHourlyAsync(archive, SeriesA, from, to);
        // series B has no value on the gas day of 28 March
        await InsertHourlyAsync(archive, SeriesB, from, ViennaUtc(2026, 3, 28, 6));
        await InsertHourlyAsync(archive, SeriesB, ViennaUtc(2026, 3, 29, 6), to);
        await RefreshAsync(archive);

        var bins = Bins(await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId,
            LocalDays("Value", from, to, 6).WithGroupByColumns(["rtId"])), "value");

        var axis = new[] { from, ViennaUtc(2026, 3, 28, 6), ViennaUtc(2026, 3, 29, 6) };
        Assert.Equal(axis, bins.Where(b => b.RtId == SeriesA).Select(b => b.Start));
        Assert.Equal(axis, bins.Where(b => b.RtId == SeriesB).Select(b => b.Start));
        Assert.Equal([24L, 23L, 24L], bins.Where(b => b.RtId == SeriesA).Select(b => b.Count));
        Assert.Equal([24L, 0L, 24L], bins.Where(b => b.RtId == SeriesB).Select(b => b.Count));
        Assert.Null(bins.Single(b => b.RtId == SeriesB && b.Count == 0).Sum);
        Assert.Equal(6, bins.Count);
    }

    [Fact]
    public async Task AGasDayWithoutAnyValue_IsABinWithCount0_AndSumNull()
    {
        var archive = await CreateRawAsync();
        var from = ViennaUtc(2026, 6, 1, 6);
        await InsertHourlyAsync(archive, SeriesA, from, from.AddDays(1));
        await InsertHourlyAsync(archive, SeriesA, from.AddDays(2), from.AddDays(3));
        await RefreshAsync(archive);

        var bins = Bins(await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId,
            LocalDays("Value", from, from.AddDays(3), 6)), "value");

        Assert.Equal([24L, 0L, 24L], bins.Select(b => b.Count));
        Assert.Null(bins[1].Sum);
        Assert.Equal(24.0, Convert.ToDouble(bins[0].Sum, CultureInfo.InvariantCulture));
    }

    // ---- calendar days from a time-range archive per rtId (AB#7353, comment 2026-10-27) -----------------------------

    [Fact]
    public async Task AYearOf15MinuteTimeRangeData_InViennaCalendarDays_PerRtId_Is92_100_And96()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, rtId.ToString());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
            [new ArchiveColumnDdl("Amount", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true)], 2, 0));
        var from = ViennaUtc(2026, 1, 1);
        var to = ViennaUtc(2027, 1, 1);
        var windows = (int)((to - from).TotalMinutes / 15);
        foreach (var series in new[] { SeriesA, SeriesB })
        {
            await fixture.SqlAsync(
                $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"amount\") " +
                $"SELECT CAST({Ms(from)} + CAST(generate_series AS BIGINT) * 900000 AS TIMESTAMP WITH TIME ZONE), " +
                $"CAST({Ms(from)} + (CAST(generate_series AS BIGINT) + 1) * 900000 AS TIMESTAMP WITH TIME ZONE), '{series}', '{TypeLiteral}', 0.25 " +
                $"FROM generate_series(0, {windows - 1})");
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var snapshot = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "tr", [new CkArchiveColumnSpec("Amount", true, false)])
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15)
        };

        var bins = Bins(await Repository(tenant, rtId, snapshot).ExecuteDownsamplingQueryAsync(rtId,
            LocalDays("Amount", from, to, 0).WithGroupByColumns(["rtId"])), "amount");

        Assert.Equal(35_040, windows);
        foreach (var series in new[] { SeriesA, SeriesB })
        {
            var days = bins.Where(b => b.RtId == series).ToList();
            Assert.Equal(365, days.Count);
            Assert.All(days, d => Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTimeFromUtc(d.Start, Vienna).TimeOfDay));
            Assert.Equal(92, days.Single(d => d.Start == ViennaUtc(2026, 3, 29)).Count);
            Assert.Equal(100, days.Single(d => d.Start == ViennaUtc(2026, 10, 25)).Count);
            Assert.All(days.Where(d => d.Start != ViennaUtc(2026, 3, 29) && d.Start != ViennaUtc(2026, 10, 25)),
                d => Assert.Equal(96, d.Count));
            Assert.Equal(windows, days.Sum(d => d.Count));
        }
    }

    // ---- criterion 6: performance (not in the CI, AB#8004) ----------------------------------------------------------

    [Fact]
    [Trait("Category", "Performance")]
    public async Task OneYearOfHourlyDataFor50Series_GasDaysPerRtId_ReturnInAtMostOneSecond()
    {
        var archive = await CreateRawAsync();
        var from = ViennaUtc(2026, 1, 1, 6);
        var to = ViennaUtc(2027, 1, 1, 6);
        var hours = (int)(to - from).TotalHours;
        var series = Enumerable.Range(0, 50).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        foreach (var rtId in series)
        {
            await InsertHourlyAsync(archive, rtId, from, to);
        }

        await RefreshAsync(archive);
        Assert.Equal(50L * hours, await fixture.CountAsync($"SELECT COUNT(*) FROM {archive.Table}"));
        var options = LocalDays("Value", from, to, 6).WithGroupByColumns(["rtId"]);

        await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId, options); // warm-up
        var timings = new List<double>();
        StreamDataQueryResult? result = null;
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            result = await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId, options);
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"AB#7360 perf: {50L * hours} raw rows, 50 series, 365 gas days: median {timings[2]:F0} ms, max {timings[^1]:F0} ms");
        Assert.Equal(50 * 365, result!.Rows.Count);
        Assert.True(timings[2] <= 1000, $"median {timings[2]:F0} ms exceeds the 1 s budget");
    }

    /// <summary>The bin start as a UTC instant; the repository returns local-day bins as UTC instants.</summary>
    private static DateTime AsUtc(DateTime value)
    {
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        return value;
    }

    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);
}
