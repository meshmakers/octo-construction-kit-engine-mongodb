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

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#7727 against a real CrateDB: every rollup row reports <c>manual_count</c> and <c>last_manual_at</c> as platform
///     columns, without a change to the rollup definition. The ladder here is a time-range archive with an hourly rollup
///     and a daily rollup over it, driven through the repository: writes, forward aggregation, recompute and reads are
///     the production code.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupManualColumnsIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const string ManualCount = "manual_count";
    private const string LastManualAt = "last_manual_at";

    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId Meter = OctoObjectId.GenerateNewId();
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset PlatformNow = new(2026, 10, 30, 9, 30, 0, TimeSpan.Zero);

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkRollupAggregationSpec[] Specs = [new("Value", CkRollupFunction.Sum, "value_sum")];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Rung(ArchiveSnapshot Archive, RollupArchiveSnapshot Rollup)
    {
        public OctoObjectId RtId => Archive.RtId;
    }

    private sealed record Ladder(
        string Tenant, ArchiveSnapshot Base, Rung Hourly, Rung Daily, CrateDbStreamDataRepository Repository,
        Func<RecomputeProcessState, CrateDbStreamDataRepository> NewRepository)
    {
        public string Table(OctoObjectId rtId) => TenantSchema.QualifiedArchiveTable(Tenant, rtId.ToString());
    }

    private static Rung NewRung(OctoObjectId sourceRtId, TimeSpan bucket, CkRollupAggregationSpec[]? specs = null)
    {
        var rtId = OctoObjectId.GenerateNewId();
        specs ??= Specs;
        return new Rung(
            new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = bucket
            },
            new RollupArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null,
                [new RollupSourceReference(sourceRtId)], bucket, TimeSpan.FromMinutes(5), null, specs, null));
    }

    private async Task<Ladder> CreateLadderAsync(bool rawBase = false, CkRollupAggregationSpec[]? specs = null)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var baseRtId = OctoObjectId.GenerateNewId();
        var baseSnapshot = new ArchiveSnapshot(baseRtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = !rawBase,
            Period = rawBase ? null : TimeSpan.FromMinutes(15)
        };
        var hourly = NewRung(baseRtId, TimeSpan.FromHours(1), specs);
        var daily = NewRung(hourly.RtId, TimeSpan.FromDays(1), specs);

        var archives = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archives.GetAsync(baseRtId)).Returns(baseSnapshot);
        A.CallTo(() => archives.GetAsync(hourly.RtId)).Returns(hourly.Archive);
        A.CallTo(() => archives.GetAsync(daily.RtId)).Returns(daily.Archive);
        var rollups = A.Fake<IRollupArchiveRuntimeStore>();
        A.CallTo(() => rollups.GetAsync(hourly.RtId)).Returns(hourly.Rollup);
        A.CallTo(() => rollups.GetAsync(daily.RtId)).Returns(daily.Rollup);

        CrateDbStreamDataRepository NewRepository(RecomputeProcessState state) => new(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, archives, A.Fake<IFormulaEngine>(),
            rollupArchiveStore: rollups, recomputeProcessState: state, timeProvider: new FixedClock(PlatformNow));

        var ladder = new Ladder(tenant, baseSnapshot, hourly, daily, NewRepository(new RecomputeProcessState()), NewRepository);

        await fixture.SqlAsync(rawBase
            ? ArchiveDdlGenerator.GenerateCreateTable(ladder.Table(baseRtId), ColumnDdl, 1, 0)
            : ArchiveDdlGenerator.GenerateCreateWindowedTable(ladder.Table(baseRtId), ColumnDdl, 1, 0));
        foreach (var rung in new[] { hourly, daily })
        {
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(ladder.Table(rung.RtId),
                RollupColumnTypeResolver.Resolve(rung.Archive.Columns, rung.Rollup.Aggregations), 1, 0, includeGeneration: true));
            await fixture.SqlAsync(GenerationMapSqlBuilder.BuildCreateTable(GenerationMapSqlBuilder.GenMapTable(tenant, rung.RtId.ToString())));
        }

        return ladder;
    }

    /// <summary>A table as an older platform version left it: without the given platform columns.</summary>
    private async Task DropColumnsAsync(string table, params string[] columns)
    {
        foreach (var column in columns)
        {
            await fixture.SqlAsync($"ALTER TABLE {table} DROP COLUMN \"{column}\"");
        }
    }

    private async Task<IReadOnlySet<string>> ColumnsOfAsync(string table)
    {
        var tableName = table.Split('.')[1].Trim('"');
        var rows = await fixture.QueryAsync($"SELECT column_name FROM information_schema.columns WHERE table_name = '{tableName}'");
        return rows.Select(r => (string)r["column_name"]!).ToHashSet();
    }

    private static TimeRangeStreamDataPoint Point(DateTime from, double value, ArchiveRowOrigin origin = ArchiveRowOrigin.Automated) => new()
    {
        RtId = Meter,
        CkTypeId = Type,
        From = from,
        To = from.AddMinutes(15),
        Attributes = new Dictionary<string, object?> { ["Value"] = value },
        Origin = origin,
        ManualWrittenBy = "user-1"
    };

    /// <summary>Every 15-minute window of the day, delivered automatically with value 1.</summary>
    private static TimeRangeStreamDataPoint[] DeliveredDay()
        => Enumerable.Range(0, 96).Select(i => Point(Day.AddMinutes(15 * i), 1)).ToArray();

    private async Task WriteAsync(Ladder ladder, params TimeRangeStreamDataPoint[] points)
    {
        await ladder.Repository.InsertTimeRangeAsync(ladder.Base.RtId, points, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Base.RtId)}");
    }

    private async Task AggregateHoursAsync(Ladder ladder)
    {
        for (var hour = 0; hour < 24; hour++)
        {
            await ladder.Repository.AggregateBucketAsync(ladder.Base, ladder.Hourly.Rollup, Day.AddHours(hour), Day.AddHours(hour + 1), Ct);
        }

        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Hourly.RtId)}");
    }

    private async Task AggregateDayAsync(Ladder ladder)
    {
        await ladder.Repository.AggregateBucketAsync(ladder.Hourly.Archive, ladder.Daily.Rollup, Day, Day.AddDays(1), Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Daily.RtId)}");
    }

    private async Task RecomputeHourAsync(Ladder ladder, int hour)
    {
        await ladder.Repository.ExecuteAsync(ladder.Base, ladder.Hourly.Rollup, Day.AddHours(hour), Day.AddHours(hour + 1), null, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Hourly.RtId)}");
    }

    private async Task RecomputeDayAsync(Ladder ladder)
    {
        await ladder.Repository.ExecuteAsync(ladder.Hourly.Archive, ladder.Daily.Rollup, Day, Day.AddDays(1), null, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Daily.RtId)}");
    }

    /// <summary>What a reader gets: the rows of the rollup through the query path, by the bucket start.</summary>
    private async Task<Dictionary<DateTime, IReadOnlyDictionary<string, object?>>> ReadAsync(Ladder ladder, Rung rung)
    {
        var result = await ladder.Repository.ExecuteQueryAsync(rung.RtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithColumns(["window_start", "value_sum", ManualCount, LastManualAt])
            .WithTimeRange(Day, Day.AddDays(1))
            .WithSortOrders([new SortOrderItem("window_start", SortOrders.Ascending)]));
        return result.Rows.ToDictionary(r => ToUtc(r.Values["window_start"]), r => (IReadOnlyDictionary<string, object?>)r.Values);
    }

    private static DateTime ToUtc(object? value) => value switch
    {
        DateTime dt => dt.ToUniversalTime(),
        DateTimeOffset dto => dto.UtcDateTime,
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        _ => throw new InvalidOperationException($"Not a timestamp: {value}")
    };

    private static long CountOf(IReadOnlyDictionary<string, object?> row) => Convert.ToInt64(row[ManualCount]);

    [Fact]
    public async Task EveryBucketReportsItsManualRows_AndTheLadderAddsThemUp()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        // Two corrections in hour 2, one in hour 5, each replacing a delivered window.
        await WriteAsync(ladder,
            Point(Day.AddHours(2), 5, ArchiveRowOrigin.Manual),
            Point(Day.AddHours(2).AddMinutes(30), 5, ArchiveRowOrigin.Manual),
            Point(Day.AddHours(5).AddMinutes(45), 5, ArchiveRowOrigin.Manual));

        await AggregateHoursAsync(ladder);
        await AggregateDayAsync(ladder);

        var hours = await ReadAsync(ladder, ladder.Hourly);
        Assert.Equal(2, CountOf(hours[Day.AddHours(2)]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(hours[Day.AddHours(2)][LastManualAt]));
        Assert.Equal(12d, Convert.ToDouble(hours[Day.AddHours(2)]["value_sum"])); // 5 + 1 + 5 + 1: replaced, not added
        Assert.Equal(0, CountOf(hours[Day.AddHours(3)]));
        Assert.Null(hours[Day.AddHours(3)][LastManualAt]);

        var day = (await ReadAsync(ladder, ladder.Daily))[Day];
        Assert.Equal(3, CountOf(day));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(day[LastManualAt]));
        Assert.Equal(96d + 3 * 4, Convert.ToDouble(day["value_sum"]));
    }

    [Fact]
    public async Task ACorrectionInAClosedWindow_ReachesTheWholeLadder_AndCountsOnce_AlsoAfterASecondCorrectionAndARedelivery()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await AggregateHoursAsync(ladder);
        await AggregateDayAsync(ladder);
        Assert.Equal(0, CountOf((await ReadAsync(ladder, ladder.Daily))[Day]));

        await WriteAsync(ladder, Point(Day.AddHours(7), 5, ArchiveRowOrigin.Manual));
        await RecomputeHourAsync(ladder, 7);
        await RecomputeDayAsync(ladder);
        Assert.Equal(1, CountOf((await ReadAsync(ladder, ladder.Daily))[Day]));

        await WriteAsync(ladder, Point(Day.AddHours(7), 6, ArchiveRowOrigin.Manual)); // a second correction of the same window
        await WriteAsync(ladder, Point(Day.AddHours(7), 9));                         // a later delivery, which loses
        await RecomputeHourAsync(ladder, 7);
        await RecomputeDayAsync(ladder);

        var hour7 = (await ReadAsync(ladder, ladder.Hourly))[Day.AddHours(7)];
        Assert.Equal(1, CountOf(hour7));
        Assert.Equal(6d + 3, Convert.ToDouble(hour7["value_sum"]));
        var day = (await ReadAsync(ladder, ladder.Daily))[Day];
        Assert.Equal(1, CountOf(day));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(day[LastManualAt]));
    }

    // AB#7727: the rows of an existing rollup hold NULL until they are recomputed. Parent buckets over a mix must be right.
    [Fact]
    public async Task ABucketOverFilledAndEmptySourceRows_IsRight_AndStaysRightAfterOneSourceWindowIsRecomputed()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await WriteAsync(ladder,
            Point(Day.AddHours(3), 5, ArchiveRowOrigin.Manual),
            Point(Day.AddHours(9), 5, ArchiveRowOrigin.Manual));
        await AggregateHoursAsync(ladder);
        var hourly = ladder.Table(ladder.Hourly.RtId);
        // The whole rung as an update leaves it, then only hour 3 comes back through a recompute.
        await fixture.SqlAsync($"UPDATE {hourly} SET \"{ManualCount}\" = NULL, \"{LastManualAt}\" = NULL");
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");

        await RecomputeHourAsync(ladder, 3);
        await AggregateDayAsync(ladder);

        // The recomputed hour went through staging and the generation flip with its columns.
        var flipped = await fixture.QueryAsync($"SELECT \"generation\", \"{ManualCount}\" FROM {hourly} " +
                                               $"WHERE \"window_start\" = '{Day.AddHours(3):O}'::timestamp AND \"generation\" > 0");
        Assert.Equal(1L, Convert.ToInt64(Assert.Single(flipped)[ManualCount]));

        var hours = await ReadAsync(ladder, ladder.Hourly);
        Assert.Equal(1, CountOf(hours[Day.AddHours(3)]));
        Assert.Equal(0, CountOf(hours[Day.AddHours(9)]));  // not recomputed yet: empty reads as no manual values
        Assert.Equal(0, CountOf(hours[Day.AddHours(4)]));
        var day = (await ReadAsync(ladder, ladder.Daily))[Day];
        Assert.Equal(1, CountOf(day));

        await RecomputeHourAsync(ladder, 9);
        await RecomputeDayAsync(ladder);

        Assert.Equal(2, CountOf((await ReadAsync(ladder, ladder.Daily))[Day]));
    }

    [Fact]
    public async Task RowsWrittenBeforeOriginTracking_CountAsDelivered()
    {
        var ladder = await CreateLadderAsync();
        await fixture.SqlAsync(
            $"INSERT INTO {ladder.Table(ladder.Base.RtId)} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value\") " +
            $"VALUES ('{Day:O}'::timestamp, '{Day.AddMinutes(15):O}'::timestamp, '{Meter}', '{Type.SemanticVersionedFullName}', FALSE, 4)");
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Base.RtId)}");

        await AggregateHoursAsync(ladder);

        var hour0 = (await ReadAsync(ladder, ladder.Hourly))[Day];
        Assert.Equal(0, CountOf(hour0));
        Assert.Null(hour0[LastManualAt]);
    }

    [Fact]
    public async Task ASourceTableWithoutOriginColumns_DoesNotStopTheRollup_ForwardOrRecompute()
    {
        var ladder = await CreateLadderAsync();
        var baseTable = ladder.Table(ladder.Base.RtId);
        await fixture.SqlAsync(
            $"INSERT INTO {baseTable} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value\") " +
            $"VALUES ('{Day:O}'::timestamp, '{Day.AddMinutes(15):O}'::timestamp, '{Meter}', '{Type.SemanticVersionedFullName}', FALSE, 4)");
        await DropColumnsAsync(baseTable, "row_origin", "manual_written_by", "manual_written_at", "manual_reason");
        await fixture.SqlAsync($"REFRESH TABLE {baseTable}");

        await ladder.Repository.AggregateBucketAsync(ladder.Base, ladder.Hourly.Rollup, Day, Day.AddHours(1), Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Hourly.RtId)}");

        Assert.Contains("row_origin", await ColumnsOfAsync(baseTable));
        Assert.Equal(0, CountOf((await ReadAsync(ladder, ladder.Hourly))[Day]));

        // The same for the recompute path, which runs in a process of its own memo.
        var second = await CreateLadderAsync();
        var secondBase = second.Table(second.Base.RtId);
        await DropColumnsAsync(secondBase, "row_origin", "manual_written_by", "manual_written_at", "manual_reason");
        await second.Repository.ExecuteAsync(second.Base, second.Hourly.Rollup, Day, Day.AddHours(1), null, Ct);

        Assert.Contains("manual_written_at", await ColumnsOfAsync(secondBase));
    }

    [Fact]
    public async Task AnExistingRollupTable_GetsTheColumnsOnItsOwn_FromTheTick_AndFromTheFirstAggregation_AndAReadWorksBeforeAnyRecompute()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await AggregateHoursAsync(ladder);
        var hourly = ladder.Table(ladder.Hourly.RtId);
        await DropColumnsAsync(hourly, ManualCount, LastManualAt);
        var fresh = ladder.NewRepository(new RecomputeProcessState());

        await fresh.EnsureRollupPlatformColumnsAsync(ladder.Hourly.RtId, Ct); // what the orchestrator tick does
        await fresh.EnsureRollupPlatformColumnsAsync(ladder.Hourly.RtId, Ct); // idempotent

        Assert.Equal(0, CountOf((await ReadAsync(ladder, ladder.Hourly))[Day]));

        // From the first aggregation, when the source of the bucket is an existing rollup and so is the target.
        var hourlyIsSource = ladder.Table(ladder.Daily.RtId);
        await DropColumnsAsync(hourly, ManualCount, LastManualAt);
        await DropColumnsAsync(hourlyIsSource, ManualCount, LastManualAt);
        await ladder.NewRepository(new RecomputeProcessState()).AggregateBucketAsync(
            ladder.Hourly.Archive, ladder.Daily.Rollup, Day, Day.AddDays(1), Ct);

        Assert.Contains(ManualCount, await ColumnsOfAsync(hourly));
        Assert.Contains(LastManualAt, await ColumnsOfAsync(hourlyIsSource));
    }

    // Several service replicas add the columns at once while a statement of the older version keeps writing the table.
    [Fact]
    public async Task TheColumnsCanBeAddedByManyReplicas_WhileAnOlderRecomputeKeepsWritingTheTable()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await AggregateHoursAsync(ladder);
        var hourly = ladder.Table(ladder.Hourly.RtId);
        await DropColumnsAsync(hourly, ManualCount, LastManualAt);

        using var stop = new CancellationTokenSource();
        var generation = 100;
        // The copy step of a recompute of the previous version: it names the columns it knows of.
        var olderCopy = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var g = ++generation;
                await fixture.SqlAsync(
                    $"INSERT INTO {hourly} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value_sum\", \"generation\") " +
                    $"SELECT \"window_start\", \"window_end\", \"rtid\", \"cktypeid\", TRUE, \"value_sum\", {g} FROM {hourly} WHERE \"generation\" = 0");
            }
        }, Ct);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            ladder.NewRepository(new RecomputeProcessState()).EnsureRollupPlatformColumnsAsync(ladder.Hourly.RtId, Ct)));
        await Task.Delay(200, Ct);
        await stop.CancelAsync();
        await olderCopy;

        Assert.Contains(ManualCount, await ColumnsOfAsync(hourly));
        Assert.Contains(LastManualAt, await ColumnsOfAsync(hourly));
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");
        Assert.True(generation > 100);
        // The rows of the older statement hold NULL, which reads as no manual values.
        Assert.Equal(0, CountOf((await ReadAsync(ladder, ladder.Hourly))[Day]));
    }

    // AB#5157: one rollup, two sources per validity span; a base archive and a rollup bucket read differently.
    [Fact]
    public async Task AMultiSourceRollup_ReadsTheColumnsTheWayEachSourceHasThem()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await WriteAsync(ladder, Point(Day.AddHours(1), 5, ArchiveRowOrigin.Manual));
        await AggregateHoursAsync(ladder);
        var nextDay = Day.AddDays(1);
        await WriteAsync(ladder, Point(nextDay, 5, ArchiveRowOrigin.Manual));

        await ladder.Repository.AggregateBucketAsync(ladder.Hourly.Archive, ladder.Daily.Rollup, Day, nextDay, Ct);
        await ladder.Repository.AggregateBucketAsync(ladder.Base, ladder.Daily.Rollup, nextDay, nextDay.AddDays(1), Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Daily.RtId)}");

        var rows = await fixture.QueryAsync($"SELECT \"window_start\", \"{ManualCount}\" FROM {ladder.Table(ladder.Daily.RtId)} ORDER BY 1");
        Assert.Equal([1L, 1L], rows.Select(r => Convert.ToInt64(r[ManualCount])));
    }

    // The LOCF statement of a time-weighted aggregation and the rank sub-select of First: the carry-in row before the
    // bucket is not one of its values, and both statements have to run on CrateDB.
    [Fact]
    public async Task OverARawSource_TheCarryInRowIsNotCounted_InTheLocfAndTheRankStatements()
    {
        CkRollupAggregationSpec[] specs = [new("Value", CkRollupFunction.TimeWeightedAvg, "value_twa"), new("Value", CkRollupFunction.First, "value_first")];
        var ladder = await CreateLadderAsync(rawBase: true, specs);
        var raw = ladder.Base.RtId;
        await ladder.Repository.InsertAsync(raw, new StreamDataPoint
        {
            RtId = Meter, CkTypeId = Type, Timestamp = Day.AddMinutes(-10),
            Attributes = new Dictionary<string, object?> { ["Value"] = 1d },
            Origin = ArchiveRowOrigin.Manual, ManualWrittenBy = "user-1"
        });
        await ladder.Repository.InsertAsync(raw, new StreamDataPoint
        {
            RtId = Meter, CkTypeId = Type, Timestamp = Day.AddMinutes(10),
            Attributes = new Dictionary<string, object?> { ["Value"] = 2d },
            Origin = ArchiveRowOrigin.Manual, ManualWrittenBy = "user-1"
        });
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(raw)}");

        await ladder.Repository.AggregateBucketAsync(ladder.Base, ladder.Hourly.Rollup, Day, Day.AddHours(1), Ct);
        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(ladder.Hourly.RtId)}");

        var row = Assert.Single(await fixture.QueryAsync(
            $"SELECT \"{ManualCount}\", \"{LastManualAt}\", \"value_first\" FROM {ladder.Table(ladder.Hourly.RtId)}"));
        Assert.Equal(1L, Convert.ToInt64(row[ManualCount]));
        Assert.Equal(2d, Convert.ToDouble(row["value_first"]));
        Assert.NotNull(row[LastManualAt]);
    }

    [Fact]
    public async Task TheColumnsCanBeFilteredAndAggregated_AndARollupHasNoRowOrigin()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await WriteAsync(ladder, Point(Day.AddHours(1), 5, ArchiveRowOrigin.Manual), Point(Day.AddHours(1).AddMinutes(15), 5, ArchiveRowOrigin.Manual));
        await AggregateHoursAsync(ladder);
        var hourly = ladder.Table(ladder.Hourly.RtId);
        await fixture.SqlAsync($"UPDATE {hourly} SET \"{ManualCount}\" = NULL WHERE \"window_start\" = '{Day.AddHours(4):O}'::timestamp");
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");

        var filtered = await ladder.Repository.ExecuteQueryAsync(ladder.Hourly.RtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithColumns([ManualCount])
            .WithTimeRange(Day, Day.AddDays(1))
            .WithFieldFilters([new FieldFilter(ManualCount, FieldFilterOperator.GreaterThan, 0)]));
        Assert.Equal(2L, Convert.ToInt64(Assert.Single(filtered.Rows).Values[ManualCount]));

        var aggregated = await ladder.Repository.ExecuteAggregationQueryAsync(ladder.Hourly.RtId, StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithTimeRange(Day, Day.AddDays(1))
            .WithAggregationColumns([new AggregationColumn(ManualCount, AggregationFunction.Sum), new AggregationColumn(LastManualAt, AggregationFunction.Maximum)]));
        var totals = Assert.Single(aggregated.Rows).Values;
        Assert.Equal(2L, Convert.ToInt64(totals["manual_count_sum"]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(totals["last_manual_at_max"]));

        var fields = StreamDataFieldResolver.CreateForArchive(ladder.Hourly.Archive);
        Assert.NotNull(fields.Resolve(ManualCount));
        Assert.NotNull(fields.Resolve(LastManualAt));
        Assert.Null(fields.Resolve("row_origin"));
        Assert.Null(fields.Resolve("manual_written_at"));
    }

    [Fact]
    public async Task TheExportOfARollup_CarriesTheColumnsThroughTheImport_AndAnOlderExportImportsAsEmpty()
    {
        var ladder = await CreateLadderAsync();
        await WriteAsync(ladder, DeliveredDay());
        await WriteAsync(ladder, Point(Day.AddHours(1), 5, ArchiveRowOrigin.Manual));
        await AggregateHoursAsync(ladder);

        var exported = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in ladder.Repository.ExportRowsAsync(ladder.Hourly.RtId, null, Ct))
        {
            exported.Add(row);
        }

        var hourly = ladder.Table(ladder.Hourly.RtId);
        await fixture.SqlAsync($"DELETE FROM {hourly}");
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");
        await ladder.Repository.ImportRowsAsync(ladder.Hourly.RtId, exported.ToAsyncEnumerable(), ArchiveImportMode.Upsert, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");

        var hours = await ReadAsync(ladder, ladder.Hourly);
        Assert.Equal(1, CountOf(hours[Day.AddHours(1)]));
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(hours[Day.AddHours(1)][LastManualAt]));

        // A dump taken before the columns existed.
        var older = exported.Select(r => (IReadOnlyDictionary<string, object?>)r
            .Where(kv => kv.Key is not (ManualCount or LastManualAt)).ToDictionary()).ToList();
        await fixture.SqlAsync($"DELETE FROM {hourly}");
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");
        await ladder.Repository.ImportRowsAsync(ladder.Hourly.RtId, older.ToAsyncEnumerable(), ArchiveImportMode.Upsert, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {hourly}");

        Assert.Equal(0, CountOf((await ReadAsync(ladder, ladder.Hourly))[Day.AddHours(1)]));
    }

    // AB#5967: the native copy copies the table as it is. A dump from before the columns loads into a table that has them.
    [Theory]
    [InlineData(false, 1L)]
    [InlineData(true, 0L)]
    public async Task TheNativeCopy_CarriesTheColumns_AndAnOlderDumpLoadsAsEmpty(bool dumpFromBeforeTheColumns, long expectedCount)
    {
        var source = await CreateLadderAsync();
        await WriteAsync(source, DeliveredDay());
        await WriteAsync(source, Point(Day.AddHours(1), 5, ArchiveRowOrigin.Manual));
        await AggregateHoursAsync(source);
        if (dumpFromBeforeTheColumns)
        {
            await DropColumnsAsync(source.Table(source.Hourly.RtId), ManualCount, LastManualAt);
        }

        var target = await CreateLadderAsync();
        var location = CrateStagingCopyFixture.NewFileLocation();
        var options = new ArchiveCopyOptions();
        var plan = await source.Repository.PlanArchiveCopyAsync(source.Hourly.RtId, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        var dump = await source.Repository.DumpArchiveToStagingAsync(source.Hourly.RtId, location, options, Ct);
        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Hourly.RtId, dump.Plan, location, options, Ct);

        Assert.True(load.IsSuccess);
        await fixture.SqlAsync($"REFRESH TABLE {target.Table(target.Hourly.RtId)}");
        Assert.Equal(expectedCount, CountOf((await ReadAsync(target, target.Hourly))[Day.AddHours(1)]));
    }

    [Fact]
    public void TheColumnTypes_AreACountAndATimestamp_ForEveryRollup_AndNotPartOfTheDefinition()
    {
        var rung = NewRung(OctoObjectId.GenerateNewId(), TimeSpan.FromHours(1));

        var ddl = ArchiveDdlGenerator.GenerateCreateWindowedTable("\"t\".\"a\"",
            RollupColumnTypeResolver.Resolve(rung.Archive.Columns, Specs), 1, 0, includeGeneration: true);

        Assert.Contains($"\"{ManualCount}\" BIGINT,", ddl);
        Assert.Contains($"\"{LastManualAt}\" TIMESTAMP WITH TIME ZONE,", ddl);
        Assert.DoesNotContain(rung.Archive.Columns, c => ArchiveRowOriginFields.IsRollupField(c.Path));
    }
}
