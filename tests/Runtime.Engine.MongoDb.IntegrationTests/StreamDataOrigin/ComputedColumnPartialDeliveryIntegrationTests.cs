using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#8622 against a real CrateDB, for raw and time-range archives: a computed column of a partial delivery is
///     evaluated on the merged row (decided by Reimar 2026-11-09). An input the point does not send is taken from the
///     stored row, an input sent as <c>null</c> is null, a new row has NULL for the inputs it does not send, and a point
///     that sends no input leaves the computed column untouched. Archive: Net = Consumption - Production.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ComputedColumnPartialDeliveryIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formulas =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Consumption", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Production", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Voltage", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Net", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkArchiveColumnSpec[] Columns =
    [
        new("Consumption", false, false),
        new("Production", false, false),
        new("Voltage", false, false),
        new(string.Empty, false, false) { Name = "Net", Formula = "consumption - production", ResultType = FormulaResultType.Double }
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Archive(string Tenant, OctoObjectId RtId, bool TimeRange, ArchiveSnapshot Snapshot,
        IArchiveRuntimeStore Store, CrateDbStreamDataRepository Repository)
    {
        public string Table => TenantSchema.QualifiedArchiveTable(Tenant, RtId.ToString());
        public string TimeColumn => TimeRange ? "window_start" : "timestamp";
    }

    private async Task<Archive> CreateArchiveAsync(bool timeRange, IReadOnlyList<ArchiveConflictKey>? precedence = null)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var snapshot = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "measurements", Columns)
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null,
            ConflictPrecedence = precedence ?? []
        };
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(snapshot);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, Formulas);
        var archive = new Archive(tenant, rtId, timeRange, snapshot, store, repository);
        await fixture.SqlAsync(timeRange
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(archive.Table, ColumnDdl, 1, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(archive.Table, ColumnDdl, 1, 0));
        return archive;
    }

    private sealed record Delivery(OctoObjectId Meter, int Slot, Dictionary<string, object?> Attributes,
        ArchiveRowOrigin Origin = ArchiveRowOrigin.Automated);

    private static Dictionary<string, object?> Inputs(double? consumption, double? production) => new()
    {
        ["Consumption"] = consumption,
        ["Production"] = production
    };

    /// <param name="refresh">
    /// False to send the next write without a table refresh: the stored inputs are read by primary key, which CrateDB
    /// answers in real time.
    /// </param>
    private async Task<StreamDataWriteResult> WriteAsync(Archive archive, bool refresh, params Delivery[] deliveries)
    {
        StreamDataWriteResult result;
        if (archive.TimeRange)
        {
            result = await archive.Repository.InsertTimeRangeAsync(archive.RtId, deliveries.Select(d => new TimeRangeStreamDataPoint
            {
                RtId = d.Meter,
                CkTypeId = Type,
                From = Start.AddMinutes(15 * d.Slot),
                To = Start.AddMinutes(15 * (d.Slot + 1)),
                Attributes = d.Attributes,
                Origin = d.Origin,
                ManualWrittenBy = d.Origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
            }).ToArray(), Ct);
        }
        else
        {
            result = await archive.Repository.InsertAsync(archive.RtId, deliveries.Select(d => new StreamDataPoint
            {
                RtId = d.Meter,
                CkTypeId = Type,
                Timestamp = Start.AddMinutes(15 * d.Slot),
                Attributes = d.Attributes,
                Origin = d.Origin,
                ManualWrittenBy = d.Origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
            }).ToArray());
        }

        if (refresh)
        {
            await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        }

        return result;
    }

    private Task<StreamDataWriteResult> WriteAsync(Archive archive, params Delivery[] deliveries) =>
        WriteAsync(archive, refresh: true, deliveries);

    private async Task<(double? Consumption, double? Production, double? Voltage, double? Net)> RowAsync(Archive archive,
        OctoObjectId meter, int slot = 0)
    {
        var rows = await fixture.QueryAsync(
            $"SELECT \"consumption\", \"production\", \"voltage\", \"net\" FROM {archive.Table} " +
            $"WHERE \"rtid\" = '{meter}' AND \"{archive.TimeColumn}\" = '{Start.AddMinutes(15 * slot):yyyy-MM-ddTHH:mm:ss}Z'");
        var row = Assert.Single(rows);
        return (D(row["consumption"]), D(row["production"]), D(row["voltage"]), D(row["net"]));

        static double? D(object? v) => v is null ? null : Convert.ToDouble(v);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialInput_IsEvaluatedOnTheMergedRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Inputs(10, 4)));
        Assert.Equal((10d, 4d, null, 6d), await RowAsync(archive, meter));

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Consumption"] = 12d }));

        Assert.Equal((12d, 4d, null, 8d), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialInput_RightAfterTheFirstWrite_SeesItWithoutARefresh(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, refresh: false, new Delivery(meter, 0, Inputs(10, 4)));

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Production"] = 1d }));

        Assert.Equal((10d, 1d, null, 9d), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExplicitNullInput_TheFormulaSeesNull(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Inputs(10, 4)));

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Consumption"] = 12d, ["Production"] = null }));

        Assert.Equal((12d, null, null, null), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeliveryWithoutAnyInput_LeavesTheComputedColumnUntouched(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Inputs(10, 4)));

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Voltage"] = 230d }));

        Assert.Equal((10d, 4d, 230d, 6d), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewPartialRow_HasNullForTheInputsItDidNotSend(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Consumption"] = 12d }));

        Assert.Equal((12d, null, null, null), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMixedBatchWithDifferentInputSets_EvaluatesEveryRowOnItsMergedRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var a = OctoObjectId.GenerateNewId();
        var b = OctoObjectId.GenerateNewId();
        var c = OctoObjectId.GenerateNewId();
        var d = OctoObjectId.GenerateNewId();
        var e = OctoObjectId.GenerateNewId();
        await WriteAsync(archive,
            new Delivery(a, 0, Inputs(10, 4)),
            new Delivery(b, 0, Inputs(20, 5)),
            new Delivery(c, 0, Inputs(30, 6)),
            new Delivery(e, 0, Inputs(50, 8)));

        var result = await WriteAsync(archive,
            new Delivery(a, 0, new() { ["Consumption"] = 12d }),             // stored production 4
            new Delivery(b, 0, new() { ["Production"] = 7d }),               // stored consumption 20
            new Delivery(c, 0, Inputs(31, 1)),                               // all inputs, no read
            new Delivery(d, 0, new() { ["Production"] = 2d }),               // new row
            new Delivery(e, 0, new() { ["Voltage"] = 231d }),                // no input
            new Delivery(a, 0, new() { ["Production"] = 2d }),               // same row again: sees consumption 12
            new Delivery(a, 1, new() { ["Consumption"] = 40d }));            // new row of a known meter

        Assert.Equal(7, result.Stored);
        Assert.Equal((12d, 2d, null, 10d), await RowAsync(archive, a));
        Assert.Equal((40d, null, null, null), await RowAsync(archive, a, 1));
        Assert.Equal((20d, 7d, null, 13d), await RowAsync(archive, b));
        Assert.Equal((31d, 1d, null, 30d), await RowAsync(archive, c));
        Assert.Equal((null, 2d, null, null), await RowAsync(archive, d));
        Assert.Equal((50d, 8d, 231d, 42d), await RowAsync(archive, e));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoredManualRow_IsNotChangedByAnAutomatedPartialDelivery(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Inputs(10, 4), ArchiveRowOrigin.Manual));

        var result = await WriteAsync(archive, new Delivery(meter, 0, new() { ["Consumption"] = 12d }));

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        Assert.Equal((10d, 4d, null, 6d), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManualPartialCorrection_IsEvaluatedOnTheMergedRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Inputs(10, 4)));

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Production"] = 3d }, ArchiveRowOrigin.Manual));

        Assert.Equal((10d, 3d, null, 7d), await RowAsync(archive, meter));
    }

    [Fact]
    public async Task ARollupOverTheComputedColumn_AggregatesTheMergedValue()
    {
        var archive = await CreateArchiveAsync(timeRange: false);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive,
            new Delivery(meter, 0, Inputs(10, 4)),
            new Delivery(meter, 1, Inputs(20, 5)));
        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Consumption"] = 12d }));

        var rollupRtId = OctoObjectId.GenerateNewId();
        var specs = new[] { new CkRollupAggregationSpec("Net", CkRollupFunction.Sum, null) };
        var rollup = new RollupArchiveSnapshot(rollupRtId, Type, CkArchiveStatus.Activated, "hourly",
            [new RollupSourceReference(archive.RtId)], TimeSpan.FromHours(1), TimeSpan.Zero, Start, specs, null);
        var rollupArchive = new ArchiveSnapshot(rollupRtId, Type, CkArchiveStatus.Activated, "hourly",
            RollupColumnGenerator.Generate(specs))
        {
            RollupAggregations = specs,
            Period = TimeSpan.FromHours(1)
        };
        A.CallTo(() => archive.Store.GetAsync(rollupRtId)).Returns(rollupArchive);
        await archive.Repository.EnsureArchiveCreatedAsync(rollupArchive);

        await archive.Repository.AggregateBucketAsync(archive.Snapshot, rollup, Start, Start.AddHours(1), Ct);

        var rollupTable = TenantSchema.QualifiedArchiveTable(archive.Tenant, rollupRtId.ToString());
        await fixture.SqlAsync($"REFRESH TABLE {rollupTable}");
        var target = Assert.Single(RollupColumnGenerator.Generate(specs)).Path;
        var row = Assert.Single(await fixture.QueryAsync(
            $"SELECT \"{ColumnNameMapper.PathToColumnName(target)}\" AS v FROM {rollupTable} WHERE \"rtid\" = '{meter}'"));
        Assert.Equal(8d + 15d, Convert.ToDouble(row["v"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnsentRankingColumn_FeedsTheFormulaWithItsStoredValue(bool timeRange)
    {
        // AB#8622: a ranking column the point does not send keeps its stored value, so the formula reads the stored value
        // (stored-row read) and the guard compares the merged row. A manual point wins on $Origin; Production stays 4.
        var archive = await CreateArchiveAsync(timeRange, [new ArchiveConflictKey("Production", ConflictKeyOrder.LowerWins)]);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Inputs(10, 4)));

        var result = await WriteAsync(archive, new Delivery(meter, 0, new() { ["Consumption"] = 12d }, ArchiveRowOrigin.Manual));

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        Assert.Equal((12d, (double?)4d, (double?)null, (double?)8d), await RowAsync(archive, meter));
    }
}
