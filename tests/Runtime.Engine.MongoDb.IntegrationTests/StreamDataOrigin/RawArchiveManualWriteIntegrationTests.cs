using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
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
///     AB#8606 against a real CrateDB: manual writes into a <b>raw</b> archive (one row per rtId + ckTypeId +
///     timestamp). A manual value survives a later automated delivery of the same row with its writer and reason
///     (AB#7656 default guard), a manual write into a window a rollup has already aggregated records a dirty window
///     whose recompute replaces the delivered value in the rollup (AB#7594), and a value that does not fit its column
///     refuses the whole batch before anything is stored (AB#7643 check on the raw path).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RawArchiveManualWriteIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId Meter = OctoObjectId.GenerateNewId();
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset PlatformNow = new(2026, 11, 9, 9, 30, 0, TimeSpan.Zero);

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

    private sealed record Lane(
        string Tenant,
        ArchiveSnapshot Raw,
        ArchiveSnapshot HourlyArchive,
        RollupArchiveSnapshot Hourly,
        CrateDbStreamDataRepository Repository,
        List<ArchiveDirtyWindow> DirtyWindows)
    {
        public string Table(OctoObjectId rtId) => TenantSchema.QualifiedArchiveTable(Tenant, rtId.ToString());
    }

    /// <summary>Test/Meter with the Double attribute Value: the column the AB#7643 check reads.</summary>
    private static ICkCacheService MeterCache()
    {
        var value = new CkTypeAttributeGraph(new CkId<CkAttributeId>("Test-1.0.0/Value-1"), "Value", null,
            AttributeValueTypesDto.Double, null, null, null, null, null, isOptional: true, description: null);
        var graph = new CkTypeGraph(new CkId<CkTypeId>("Test-1.0.0/Meter-1"), isAbstract: false, isFinal: false,
            isCollectionRoot: true, [], null, null, [], [], new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
            {
                [value.CkAttributeId] = value
            }, [], new CkGraphDirectedAssociations([]), "meter", enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, Type)).Returns(graph);
        CkTypeGraph? ignored;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, Type, out ignored)).Returns(true).AssignsOutAndRefParameters(graph);
        return cache;
    }

    /// <summary>
    /// A raw archive with an hourly SUM rollup over it. <paramref name="watermark" /> is the end of the last bucket
    /// the rollup has aggregated: writes before it are retroactive and record a dirty window.
    /// </summary>
    private async Task<Lane> CreateLaneAsync(DateTime? watermark = null)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rawRtId = OctoObjectId.GenerateNewId();
        var raw = new ArchiveSnapshot(rawRtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray());

        var hourlyRtId = OctoObjectId.GenerateNewId();
        var hourlyArchive = new ArchiveSnapshot(hourlyRtId, Type, CkArchiveStatus.Activated, null,
            RollupColumnGenerator.Generate(Specs))
        {
            RollupAggregations = Specs,
            Period = TimeSpan.FromHours(1)
        };
        var hourly = new RollupArchiveSnapshot(hourlyRtId, Type, CkArchiveStatus.Activated, null,
            [new RollupSourceReference(rawRtId)], TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), watermark, Specs, null);

        var archives = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archives.GetAsync(rawRtId)).Returns(raw);
        A.CallTo(() => archives.GetAsync(hourlyRtId)).Returns(hourlyArchive);
        var rollups = A.Fake<IRollupArchiveRuntimeStore>();
        A.CallTo(() => rollups.GetAsync(hourlyRtId)).Returns(hourly);
        A.CallTo(() => rollups.EnumerateAsync()).ReturnsLazily(() => new[] { hourly }.ToAsyncEnumerable());

        var dirtyWindows = new List<ArchiveDirtyWindow>();
        var recomputeState = A.Fake<IArchiveRecomputeStateStore>();
        A.CallTo(() => recomputeState.AppendDirtyWindowAsync(rawRtId, A<ArchiveDirtyWindow>._))
            .Invokes((OctoObjectId _, ArchiveDirtyWindow window) => dirtyWindows.Add(window));

        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, MeterCache(),
            fixture.Client, fixture.Client, fixture.Configuration, tenant, archives, A.Fake<IFormulaEngine>(),
            rollupArchiveStore: rollups, recomputeStateStore: recomputeState, recomputeProcessState: new RecomputeProcessState(),
            timeProvider: new FixedClock(PlatformNow));

        var lane = new Lane(tenant, raw, hourlyArchive, hourly, repository, dirtyWindows);
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(lane.Table(rawRtId), ColumnDdl, 1, 0));
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(lane.Table(hourlyRtId),
            RollupColumnTypeResolver.Resolve(hourlyArchive.Columns, Specs), 1, 0, includeGeneration: true));
        await fixture.SqlAsync(GenerationMapSqlBuilder.BuildCreateTable(GenerationMapSqlBuilder.GenMapTable(tenant, hourlyRtId.ToString())));
        return lane;
    }

    private static StreamDataPoint Point(DateTime timestamp, object? value, ArchiveRowOrigin origin = ArchiveRowOrigin.Automated,
        string? writtenBy = null, string? reason = null) => new()
    {
        RtId = Meter,
        CkTypeId = Type,
        Timestamp = timestamp,
        Attributes = new Dictionary<string, object?> { ["Value"] = value },
        Origin = origin,
        ManualWrittenBy = writtenBy,
        ManualReason = reason
    };

    private async Task<StreamDataWriteResult> WriteAsync(Lane lane, params StreamDataPoint[] points)
    {
        var result = await lane.Repository.InsertAsync(lane.Raw.RtId, points);
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table(lane.Raw.RtId)}");
        return result;
    }

    private async Task<List<Dictionary<string, object?>>> RawRowsAsync(Lane lane)
    {
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table(lane.Raw.RtId)}");
        return await fixture.QueryAsync(
            $"SELECT \"timestamp\", \"value\", \"row_origin\", \"manual_written_by\", \"manual_written_at\", \"manual_reason\" " +
            $"FROM {lane.Table(lane.Raw.RtId)} WHERE \"rtid\" = '{Meter}' ORDER BY \"timestamp\"");
    }

    private async Task<double> HourlySumAsync(Lane lane, DateTime hour)
    {
        var result = await lane.Repository.ExecuteQueryAsync(lane.Hourly.RtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithColumns(["window_start", "value_sum"])
            .WithTimeRange(hour, hour.AddHours(1))
            .WithSortOrders([new SortOrderItem("window_start", SortOrders.Ascending)]));
        var row = result.Rows.Single(r => ToUtc(r.Values["window_start"]) == hour);
        return Convert.ToDouble(row.Values["value_sum"]);
    }

    private static DateTime ToUtc(object? value) => value switch
    {
        DateTime dt => dt.ToUniversalTime(),
        DateTimeOffset dto => dto.UtcDateTime,
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        _ => throw new InvalidOperationException($"Not a timestamp: {value}")
    };

    [Fact]
    public async Task AManualRawValue_KeepsItsValueWriterAndReason_WhenAnAutomatedDeliveryOfTheSameRowFollows()
    {
        var lane = await CreateLaneAsync();
        var timestamp = Day.AddHours(3);
        await WriteAsync(lane, Point(timestamp, 10.0));
        await WriteAsync(lane, Point(timestamp, 12.5, ArchiveRowOrigin.Manual, "jane.doe", "meter exchanged"));

        var result = await WriteAsync(lane, Point(timestamp, 99.0)); // the same rtId + ckTypeId + timestamp, automated

        Assert.Equal(0, result.Stored);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, skipped.Reason);
        var row = Assert.Single(await RawRowsAsync(lane));
        Assert.Equal(12.5, row["value"]);
        Assert.Equal("manual", row["row_origin"]);
        Assert.Equal("jane.doe", row["manual_written_by"]);
        Assert.Equal("meter exchanged", row["manual_reason"]);
        Assert.Equal(PlatformNow.UtcDateTime, ToUtc(row["manual_written_at"]));
    }

    [Fact]
    public async Task AManualRawWriteInsideAnAggregatedWindow_RecordsADirtyWindow_AndTheRecomputedRollupHasTheCorrectedValue()
    {
        var lane = await CreateLaneAsync(watermark: Day.AddDays(1));
        // Four automated readings per hour for the whole day, value 1 each.
        await WriteAsync(lane, Enumerable.Range(0, 96).Select(i => Point(Day.AddMinutes(15 * i), 1.0)).ToArray());
        for (var hour = 0; hour < 24; hour++)
        {
            await lane.Repository.AggregateBucketAsync(lane.Raw, lane.Hourly, Day.AddHours(hour), Day.AddHours(hour + 1), Ct);
        }

        await fixture.SqlAsync($"REFRESH TABLE {lane.Table(lane.Hourly.RtId)}");
        Assert.Equal(4d, await HourlySumAsync(lane, Day.AddHours(7)));
        // The rollup's watermark is set up front, so the delivered day was retroactive as well; only the window of the
        // manual write below matters here.
        lane.DirtyWindows.Clear();

        var corrected = Day.AddHours(7).AddMinutes(30);
        await WriteAsync(lane, Point(corrected, 5.0, ArchiveRowOrigin.Manual, "jane.doe", "reading corrected"));

        var window = Assert.Single(lane.DirtyWindows);
        Assert.Equal(RecomputeChangeSource.Manual, window.Source);
        Assert.True(window.WindowStart <= corrected && corrected < window.WindowEnd,
            $"The dirty window [{window.WindowStart:O}, {window.WindowEnd:O}) does not cover the corrected row {corrected:O}.");

        // What the recompute orchestrator does with the window: recompute the hourly buckets it touches.
        var from = new DateTime(window.WindowStart.Ticks - window.WindowStart.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        var to = new DateTime((window.WindowEnd.Ticks + TimeSpan.TicksPerHour - 1) / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour,
            DateTimeKind.Utc);
        await lane.Repository.ExecuteAsync(lane.Raw, lane.Hourly, from, to, null, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table(lane.Hourly.RtId)}");

        Assert.Equal(1d + 1 + 5 + 1, await HourlySumAsync(lane, Day.AddHours(7))); // replaced, not added
        Assert.Equal(4d, await HourlySumAsync(lane, Day.AddHours(6)));
    }

    [Fact]
    public async Task ARawBatchWithAValueThatDoesNotFitItsColumn_IsRefusedWhole_NamingPointAndColumn()
    {
        var lane = await CreateLaneAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => lane.Repository.InsertAsync(lane.Raw.RtId,
        [
            Point(Day, 1.0),
            Point(Day.AddMinutes(15), "twelve", ArchiveRowOrigin.Manual, "jane.doe")
        ]));

        Assert.Contains("point 1", error.Message);
        Assert.Contains($"rtId '{Meter}'", error.Message);
        Assert.Contains("column 'Value' expects a number", error.Message);
        Assert.Contains("nothing was written", error.Message);
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table(lane.Raw.RtId)}");
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table(lane.Raw.RtId)}"));
        Assert.Empty(lane.DirtyWindows);
    }
}
