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
/// AB#7360 criterion 4 ("No regression"): without <c>TimeZone</c>, <c>BinOrigin</c> and <c>BinInterval</c> a downsampling
/// query compiles to exactly the SQL of main before AB#7360 (taken on top of AB#7659, commit 6966ce9). The expected strings were
/// recorded from that commit; a change here is a behaviour change of every existing chart and needs its own work item.
/// </summary>
public class DownsamplingSqlSnapshotTests
{
    private static readonly OctoObjectId Raw = new("aa0000000000000000007360");
    private static readonly OctoObjectId TimeRange = new("aa0000000000000000007361");
    private static readonly OctoObjectId FixedRollup = new("aa0000000000000000007362");
    private static readonly OctoObjectId DayRollup = new("aa0000000000000000007363");
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId Series = new("aa00000000000000000073aa");

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly IRollupArchiveRuntimeStore _rollups = A.Fake<IRollupArchiveRuntimeStore>();
    private readonly List<string> _sql = new();

    public DownsamplingSqlSnapshotTests()
    {
        A.CallTo(() => _store.GetAsync(Raw)).Returns(new ArchiveSnapshot(Raw, Type, CkArchiveStatus.Activated, "raw",
            [new CkArchiveColumnSpec("Amount", true, false)]));
        A.CallTo(() => _store.GetAsync(TimeRange)).Returns(new ArchiveSnapshot(TimeRange, Type, CkArchiveStatus.Activated, "tr",
            [new CkArchiveColumnSpec("Amount", true, false)])
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15)
        });
        var specs = new[] { new CkRollupAggregationSpec("Amount", CkRollupFunction.Sum, null) };
        foreach (var (rtId, alignment, size) in new[]
                 {
                     (FixedRollup, BucketAlignment.FixedSize, TimeSpan.FromHours(1)),
                     (DayRollup, BucketAlignment.CalendarDay, TimeSpan.FromDays(1))
                 })
        {
            A.CallTo(() => _store.GetAsync(rtId)).Returns(
                new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(specs))
                {
                    RollupAggregations = specs,
                    Period = size
                });
            A.CallTo(() => _rollups.GetAsync(rtId)).Returns(
                new RollupArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, [new RollupSourceReference(Raw)], size,
                    TimeSpan.Zero, null, specs, null)
                {
                    BucketAlignment = alignment,
                    ReferenceTimeZone = alignment == BucketAlignment.CalendarDay ? "Europe/Vienna" : null
                });
        }

        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _sql.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>()));
    }

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(), _db,
            A.Fake<IStreamDataDatabaseManagementClient>(), Options.Create(new StreamDataConfiguration { ConnectionString = "Host=x" }),
            "snap", _store, A.Fake<IFormulaEngine>(), _rollups);

    private static StreamDataDownsamplingQueryOptions Query(DateTime from, DateTime to, int limit,
        AggregationFunction function = AggregationFunction.Average) =>
        StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithAggregationColumns([new AggregationColumn("Amount", function)])
            .WithTimeRange(from, to)
            .WithLimit(limit);

    private static readonly DateTime Day = new(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Raw_FixedWidthBins_ProbeAndQueryAreUnchanged()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(Raw, Query(Day, Day.AddDays(1), 24));

        Assert.Equal(
        [
            "SELECT COUNT(DISTINCT d.\"timestamp\") AS \"c\" FROM \"snap\".\"archive_aa0000000000000000007360\" AS d WHERE 1 = 1 AND d.\"cktypeid\" = 'Test/Meter' AND d.\"timestamp\" >= '2026-03-28 00:00:00.000Z' AND d.\"timestamp\" <= '2026-03-29 00:00:00.000Z'",
            "SELECT DATE_BIN('3600 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) AS \"T\", AVG(d.\"amount\") AS \"Avg_amount\", COUNT(d.\"timestamp\") AS \"__binCount\" FROM \"snap\".\"archive_aa0000000000000000007360\" AS d WHERE 1 = 1 AND d.\"cktypeid\" = 'Test/Meter' AND d.\"timestamp\" >= '2026-03-28 00:00:00.000Z' AND d.\"timestamp\" <= '2026-03-29 00:00:00.000Z' GROUP BY DATE_BIN('3600 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) ORDER BY DATE_BIN('3600 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) ASC"
        ], _sql);
    }

    [Fact]
    public async Task Raw_GroupedByRtIdWithAnRtIdScope_IsUnchanged()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(Raw,
            Query(Day, Day.AddDays(1), 24, AggregationFunction.Count).WithGroupByColumns(["rtId"]).WithRtIds([Series]));

        Assert.Equal(
            "SELECT DATE_BIN('3600 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) AS \"T\", d.\"rtid\" AS \"rtid\", COUNT(d.\"amount\") AS \"Count_amount\", COUNT(d.\"timestamp\") AS \"__binCount\" FROM \"snap\".\"archive_aa0000000000000000007360\" AS d WHERE 1 = 1 AND d.\"cktypeid\" = 'Test/Meter' AND d.\"timestamp\" >= '2026-03-28 00:00:00.000Z' AND d.\"timestamp\" <= '2026-03-29 00:00:00.000Z' AND d.\"rtid\" IN ('aa00000000000000000073aa') GROUP BY DATE_BIN('3600 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 00:00:00.000Z'::TIMESTAMP), d.\"rtid\" ORDER BY DATE_BIN('3600 seconds'::INTERVAL, d.\"timestamp\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) ASC, d.\"rtid\" ASC",
            _sql.Last());
    }

    [Fact]
    public async Task TimeRange_GrainRoute_IsUnchanged()
    {
        var year = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(year, year.AddYears(1), 365, AggregationFunction.Count));

        Assert.Equal(
            "SELECT DATE_BIN('86400 seconds'::INTERVAL, d.\"window_start\", '2026-01-01 00:00:00.000Z'::TIMESTAMP) AS \"T\", COUNT(d.\"amount\") AS \"Count_amount\", COUNT(d.\"window_end\") AS \"__binCount\" FROM \"snap\".\"archive_aa0000000000000000007361\" AS d WHERE 1 = 1 AND d.\"window_end\" <= DATE_BIN('86400 seconds'::INTERVAL, d.\"window_start\", '2026-01-01 00:00:00.000Z'::TIMESTAMP) + '86400 seconds'::INTERVAL AND d.\"cktypeid\" = 'Test/Meter' AND d.\"window_start\" < '2027-01-01 00:00:00.000Z' AND d.\"window_end\" > '2026-01-01 00:00:00.000Z' GROUP BY DATE_BIN('86400 seconds'::INTERVAL, d.\"window_start\", '2026-01-01 00:00:00.000Z'::TIMESTAMP) ORDER BY DATE_BIN('86400 seconds'::INTERVAL, d.\"window_start\", '2026-01-01 00:00:00.000Z'::TIMESTAMP) ASC",
            _sql.Last());
        // AB#8962: the time-range grain route first probes the phase of the stored windows (same source filters);
        // the downsampling SQL itself is unchanged when From already sits on a window start.
        Assert.Equal(
            "SELECT MIN(d.\"window_start\") AS \"s\" FROM \"snap\".\"archive_aa0000000000000000007361\" AS d WHERE 1 = 1 AND d.\"cktypeid\" = 'Test/Meter' AND d.\"window_start\" < '2027-01-01 00:00:00.000Z' AND d.\"window_end\" > '2026-01-01 00:00:00.000Z'",
            _sql.First());
        Assert.Equal(2, _sql.Count);
    }

    [Fact]
    public async Task TimeRange_RelativeWindow_SnapsTheOriginToTheProbedWindowPhase()
    {
        // AB#8962: "last 2 days" starting at 18:37:42 over 15-minute windows stored at :00/:15/:30/:45. The probe
        // returns a stored window start; the DATE_BIN origin and the lower source bound move to 18:30.
        var from = new DateTime(2026, 3, 28, 18, 37, 42, DateTimeKind.Utc);
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>.That.StartsWith("SELECT MIN(")))
            .Invokes((string _, string sql) => _sql.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>
            {
                new(new Dictionary<string, object?> { ["s"] = new DateTime(2026, 3, 28, 18, 30, 0, DateTimeKind.Utc) })
            }));

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(from, from.AddDays(2), 192, AggregationFunction.Sum));

        Assert.Equal(2, _sql.Count);
        Assert.Contains("DATE_BIN('900 seconds'::INTERVAL, d.\"window_start\", '2026-03-28 18:30:00.000Z'::TIMESTAMP)", _sql.Last());
        Assert.Contains("d.\"window_end\" > '2026-03-28 18:30:00.000Z'", _sql.Last());
    }

    [Fact]
    public async Task TimeRange_ProbeFails_KeepsFromAsTheOrigin()
    {
        var from = new DateTime(2026, 3, 28, 18, 37, 42, DateTimeKind.Utc);
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>.That.StartsWith("SELECT MIN(")))
            .Throws(new InvalidOperationException("probe down"));

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(from, from.AddDays(2), 192, AggregationFunction.Sum));

        Assert.Contains("'2026-03-28 18:37:42.000Z'::TIMESTAMP", Assert.Single(_sql));
    }

    [Fact]
    public async Task FixedSizeRollup_GrainRoute_IsUnchanged()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(FixedRollup, Query(Day, Day.AddDays(1), 6, AggregationFunction.Sum));

        Assert.Equal(
            "SELECT DATE_BIN('14400 seconds'::INTERVAL, d.\"window_start\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) AS \"T\", SUM(d.\"amount_sum\") AS \"amount_sum\", COUNT(d.\"window_end\") AS \"__binCount\" FROM \"snap\".\"archive_aa0000000000000000007362\" AS d WHERE 1 = 1 AND d.\"window_end\" <= DATE_BIN('14400 seconds'::INTERVAL, d.\"window_start\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) + '14400 seconds'::INTERVAL AND d.\"cktypeid\" = 'Test/Meter' AND d.\"window_start\" < '2026-03-29 00:00:00.000Z' AND d.\"window_end\" > '2026-03-28 00:00:00.000Z' AND \"generation\" = 0 GROUP BY DATE_BIN('14400 seconds'::INTERVAL, d.\"window_start\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) ORDER BY DATE_BIN('14400 seconds'::INTERVAL, d.\"window_start\", '2026-03-28 00:00:00.000Z'::TIMESTAMP) ASC",
            _sql.Last());
    }

    [Fact]
    public async Task CalendarDayRollup_BinsOnWindowStart_IsUnchanged()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(DayRollup, Query(Day, Day.AddDays(3), 3, AggregationFunction.Sum));

        Assert.Equal(
            "SELECT d.\"window_start\" AS \"T\", SUM(d.\"amount_sum\") AS \"amount_sum\", COUNT(d.\"window_end\") AS \"__binCount\" FROM \"snap\".\"archive_aa0000000000000000007363\" AS d WHERE 1 = 1 AND d.\"cktypeid\" = 'Test/Meter' AND d.\"window_start\" < '2026-03-31 00:00:00.000Z' AND d.\"window_end\" > '2026-03-28 00:00:00.000Z' AND \"generation\" = 0 GROUP BY d.\"window_start\" ORDER BY d.\"window_start\" ASC",
            _sql.Last());
    }
}
