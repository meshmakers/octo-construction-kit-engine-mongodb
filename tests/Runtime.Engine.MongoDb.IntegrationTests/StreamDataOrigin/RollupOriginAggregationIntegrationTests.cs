using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#7727 against a real CrateDB: a rollup over a time-range archive counts the manually entered rows of each
///     bucket (<c>Count</c> over <c>manual_written_at</c>) and keeps the time of the latest manual entry (<c>Max</c>),
///     and a daily rollup over the hourly one carries both forward (counts add up, the latest time stays the latest).
///     The archive writes go through the repository, so the conflict precedence decides what is stored: one row per
///     window, a manual correction replaces the delivered row and is therefore counted once.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupOriginAggregationIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId Meter = OctoObjectId.GenerateNewId();
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset PlatformNow = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkRollupAggregationSpec ManualCount = new("manual_written_at", CkRollupFunction.Count, "manual_count");
    private static readonly CkRollupAggregationSpec LastManual = new("manual_written_at", CkRollupFunction.Max, "last_manual");
    private static readonly CkRollupAggregationSpec FirstManual = new("manual_written_at", CkRollupFunction.Min, "first_manual");
    private static readonly CkRollupAggregationSpec WritersCount = new("manual_written_by", CkRollupFunction.Count, "writer_count");
    private static readonly CkRollupAggregationSpec ValueSum = new("Value", CkRollupFunction.Sum, "value_sum");
    private static readonly CkRollupAggregationSpec[] Specs = [ManualCount, LastManual, FirstManual, WritersCount, ValueSum];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Ladder(
        string Tenant,
        ArchiveSnapshot Base,
        CrateDbStreamDataRepository Repository,
        ArchiveSnapshot Hourly,
        RollupArchiveSnapshot HourlyRollup,
        ArchiveSnapshot Daily)
    {
        public string BaseTable => TenantSchema.QualifiedArchiveTable(Tenant, Base.RtId.ToString());
        public string HourlyTable => TenantSchema.QualifiedArchiveTable(Tenant, Hourly.RtId.ToString());
        public string DailyTable => TenantSchema.QualifiedArchiveTable(Tenant, Daily.RtId.ToString());
    }

    private static (ArchiveSnapshot Archive, RollupArchiveSnapshot Rollup) RollupSnapshots(
        OctoObjectId rtId, OctoObjectId sourceRtId, TimeSpan bucket)
    {
        var rollup = new RollupArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null,
            [new RollupSourceReference(sourceRtId)], bucket, TimeSpan.FromMinutes(5), null, Specs, null);
        var archive = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(Specs))
        {
            RollupAggregations = Specs,
            Period = bucket
        };
        return (archive, rollup);
    }

    private async Task<Ladder> CreateLadderAsync()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var baseRtId = OctoObjectId.GenerateNewId();
        // The energy community shape: manual rows rank first, the origin columns are system columns (not declared).
        var baseSnapshot = new ArchiveSnapshot(baseRtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15)
        };
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(baseRtId)).Returns(baseSnapshot);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>(),
            timeProvider: new FixedClock(PlatformNow));

        var (hourly, hourlyRollup) = RollupSnapshots(OctoObjectId.GenerateNewId(), baseRtId, TimeSpan.FromHours(1));
        var (daily, _) = RollupSnapshots(OctoObjectId.GenerateNewId(), hourly.RtId, TimeSpan.FromDays(1));
        var ladder = new Ladder(tenant, baseSnapshot, repository, hourly, hourlyRollup, daily);

        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(ladder.BaseTable, ColumnDdl, 1, 0));
        foreach (var (table, snapshot) in new[] { (ladder.HourlyTable, hourly), (ladder.DailyTable, daily) })
        {
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
                RollupColumnTypeResolver.Resolve(snapshot.Columns, Specs), 1, 0, includeGeneration: true));
        }

        return ladder;
    }

    private static TimeRangeStreamDataPoint Point(DateTime from, double value, ArchiveRowOrigin origin = ArchiveRowOrigin.Automated,
        string writtenBy = "user-1") => new()
    {
        RtId = Meter,
        CkTypeId = Type,
        From = from,
        To = from.AddMinutes(15),
        Attributes = new Dictionary<string, object?> { ["Value"] = value },
        Origin = origin,
        ManualWrittenBy = writtenBy
    };

    private static async Task WriteAsync(Ladder ladder, params TimeRangeStreamDataPoint[] points)
        => await ladder.Repository.InsertTimeRangeAsync(ladder.Base.RtId, points, TestContext.Current.CancellationToken);

    /// <summary>Aggregates the hourly buckets of the day from the base archive (rule 1b binding), then the daily bucket (rule 2).</summary>
    private async Task RollUpAsync(Ladder ladder)
    {
        await fixture.SqlAsync($"REFRESH TABLE {ladder.BaseTable}");
        var hourlyBinding = RollupAggregationColumns.ResolveForSource(Specs, ladder.Hourly.RtId, ladder.Base, null);
        Assert.Equal(RollupSourceColumnResolutionKind.OriginColumn, KindOf(hourlyBinding, ManualCount));
        for (var hour = 0; hour < 24; hour++)
        {
            await fixture.SqlAsync(RollupAggregationSqlBuilder.Build(ladder.BaseTable, ladder.HourlyTable, Type.ToString(),
                hourlyBinding, Day.AddHours(hour), Day.AddHours(hour + 1), sourceUsesWindowedStorage: true));
        }

        await fixture.SqlAsync($"REFRESH TABLE {ladder.HourlyTable}");
        var dailyBinding = RollupAggregationColumns.ResolveForSource(Specs, ladder.Daily.RtId, ladder.Hourly, ladder.HourlyRollup);
        Assert.Equal(RollupSourceColumnResolutionKind.ChildAggregation, KindOf(dailyBinding, ManualCount));
        await fixture.SqlAsync(RollupAggregationSqlBuilder.Build(ladder.HourlyTable, ladder.DailyTable, Type.ToString(),
            dailyBinding, Day, Day.AddDays(1), sourceUsesWindowedStorage: true, sourceGenerationRanges: []));
        await fixture.SqlAsync($"REFRESH TABLE {ladder.DailyTable}");
    }

    private static RollupSourceColumnResolutionKind KindOf(
        IReadOnlyList<RollupSourceAggregation> binding, CkRollupAggregationSpec spec)
        => binding.Single(b => b.Spec == spec).Kind;

    private async Task<Dictionary<string, object?>> HourAsync(Ladder ladder, int hour)
        => (await fixture.QueryAsync(
                $"SELECT \"manual_count\", \"last_manual\", \"first_manual\", \"writer_count\", \"value_sum\" FROM {ladder.HourlyTable} " +
                $"WHERE \"window_start\" = {new DateTimeOffset(Day.AddHours(hour)).ToUnixTimeMilliseconds()}"))
            .Single();

    private async Task<Dictionary<string, object?>> DailyAsync(Ladder ladder)
        => (await fixture.QueryAsync(
                $"SELECT \"manual_count\", \"last_manual\", \"first_manual\", \"writer_count\", \"value_sum\" FROM {ladder.DailyTable}"))
            .Single();

    private static DateTime ToUtc(object? value) => value switch
    {
        DateTime dt => dt.ToUniversalTime(),
        DateTimeOffset dto => dto.UtcDateTime,
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        _ => throw new InvalidOperationException($"Not a timestamp: {value}")
    };

    /// <summary>Every 15-minute window of the day, delivered automatically with value 1.</summary>
    private static TimeRangeStreamDataPoint[] DeliveredDay()
        => Enumerable.Range(0, 96).Select(i => Point(Day.AddMinutes(15 * i), 1)).ToArray();

    [Fact]
    public async Task EachBucketCountsItsManualRows_AndKeepsTheLatestManualEntry_UpTheLadder()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());

        // Two manual corrections in hour 2, one in hour 5, each replacing a delivered window.
        await WriteAsync(ladder,
            Point(Day.AddHours(2), 5, ArchiveRowOrigin.Manual),
            Point(Day.AddHours(2).AddMinutes(30), 5, ArchiveRowOrigin.Manual),
            Point(Day.AddHours(5).AddMinutes(45), 5, ArchiveRowOrigin.Manual, "user-2"));

        await RollUpAsync(ladder);

        var hour2 = await HourAsync(ladder, 2);
        Assert.Equal(2L, Convert.ToInt64(hour2["manual_count"]));
        Assert.Equal(2L, Convert.ToInt64(hour2["writer_count"]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(hour2["last_manual"]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(hour2["first_manual"]));
        Assert.Equal(12d, Convert.ToDouble(hour2["value_sum"])); // 5 + 1 + 5 + 1: the corrections replaced, not added

        var hour3 = await HourAsync(ladder, 3);
        Assert.Equal(0L, Convert.ToInt64(hour3["manual_count"]));
        Assert.Null(hour3["last_manual"]);
        Assert.Null(hour3["first_manual"]);

        var daily = await DailyAsync(ladder);
        Assert.Equal(3L, Convert.ToInt64(daily["manual_count"]));
        Assert.Equal(3L, Convert.ToInt64(daily["writer_count"]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(daily["last_manual"]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(daily["first_manual"]));
        Assert.Equal(96d + 3 * 4, Convert.ToDouble(daily["value_sum"]));
    }

    [Fact]
    public async Task ARecompute_CountsACorrectionOnce_EvenAfterItWasCorrectedAgainAndRedelivered()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await WriteAsync(ladder, Point(Day.AddHours(7), 5, ArchiveRowOrigin.Manual));
        await RollUpAsync(ladder);
        Assert.Equal(1L, Convert.ToInt64((await HourAsync(ladder, 7))["manual_count"]));

        // A second manual correction of the same window and a later automated delivery for it.
        await WriteAsync(ladder, Point(Day.AddHours(7), 6, ArchiveRowOrigin.Manual));
        await WriteAsync(ladder, Point(Day.AddHours(7), 9));
        await RollUpAsync(ladder);

        var hour7 = await HourAsync(ladder, 7);
        Assert.Equal(1L, Convert.ToInt64(hour7["manual_count"]));
        Assert.Equal(6d + 3, Convert.ToDouble(hour7["value_sum"])); // the manual value stays, the delivery is not stored
        Assert.Equal(1L, Convert.ToInt64((await DailyAsync(ladder))["manual_count"]));
    }

    [Fact]
    public async Task RowsWrittenBeforeOriginTracking_CountAsDelivered()
    {
        var ladder = await CreateLadderAsync();
        // A row of a table provisioned before AB#5356: the origin columns were added later and are NULL.
        await fixture.SqlAsync(
            $"INSERT INTO {ladder.BaseTable} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value\") " +
            $"VALUES ({new DateTimeOffset(Day).ToUnixTimeMilliseconds()}, {new DateTimeOffset(Day.AddMinutes(15)).ToUnixTimeMilliseconds()}, " +
            $"'{Meter}', '{Type}', FALSE, 4)");

        await RollUpAsync(ladder);

        var hour0 = await HourAsync(ladder, 0);
        Assert.Equal(0L, Convert.ToInt64(hour0["manual_count"]));
        Assert.Null(hour0["last_manual"]);
        Assert.Equal(4d, Convert.ToDouble(hour0["value_sum"]));
    }

    [Fact]
    public void TheTimestampAggregations_AreStoredAsTimestamps_AtEveryLevel()
    {
        var (daily, _) = RollupSnapshots(OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId(), TimeSpan.FromDays(1));

        var types = RollupColumnTypeResolver.Resolve(daily.Columns, Specs)
            .ToDictionary(c => c.Path, c => ((CrateColumnType.Primitive)c.Type).CrateTypeName);

        Assert.Equal("BIGINT", types["manual_count"]);
        Assert.Equal("TIMESTAMP WITH TIME ZONE", types["last_manual"]);
        Assert.Equal("TIMESTAMP WITH TIME ZONE", types["first_manual"]);
        Assert.Equal("BIGINT", types["writer_count"]);
        Assert.Equal("DOUBLE PRECISION", types["value_sum"]);
    }
}
