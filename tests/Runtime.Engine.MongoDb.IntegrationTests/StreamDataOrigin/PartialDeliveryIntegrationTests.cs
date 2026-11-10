using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#8622 against a real CrateDB, for raw and time-range archives: a write updates only the columns the point
///     names. A column the point does not name keeps its stored value, a column named with <c>null</c> is cleared, and a
///     new row stores NULL for the columns it does not name. Before, every column of the archive was bound and a partial
///     re-delivery cleared all columns it did not send.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class PartialDeliveryIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("MeterReading", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Voltage", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Quality", new CrateColumnType.Primitive("INTEGER"), false, false)
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Archive(string Tenant, OctoObjectId RtId, bool TimeRange, CrateDbStreamDataRepository Repository)
    {
        public string Table => TenantSchema.QualifiedArchiveTable(Tenant, RtId.ToString());
        public string TimeColumn => TimeRange ? "window_start" : "timestamp";
    }

    private async Task<Archive> CreateArchiveAsync(bool timeRange, IReadOnlyList<ArchiveConflictKey>? precedence = null)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null,
            ConflictPrecedence = precedence ?? []
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>());
        var archive = new Archive(tenant, rtId, timeRange, repository);
        await fixture.SqlAsync(timeRange
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(archive.Table, ColumnDdl, 1, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(archive.Table, ColumnDdl, 1, 0));
        return archive;
    }

    /// <summary>One point: <paramref name="attributes" /> names exactly the columns the point sends.</summary>
    private sealed record Delivery(OctoObjectId Meter, int Slot, Dictionary<string, object?> Attributes,
        ArchiveRowOrigin Origin = ArchiveRowOrigin.Automated);

    private static Dictionary<string, object?> Full(double reading, double? voltage, int? quality) => new()
    {
        ["MeterReading"] = reading,
        ["Voltage"] = voltage,
        ["Quality"] = quality
    };

    private async Task<StreamDataWriteResult> WriteAsync(Archive archive, params Delivery[] deliveries)
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

        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        return result;
    }

    private async Task<Dictionary<string, object?>> RowAsync(Archive archive, OctoObjectId meter, int slot = 0)
    {
        var rows = await fixture.QueryAsync(
            $"SELECT \"meterreading\", \"voltage\", \"quality\", \"row_origin\" FROM {archive.Table} " +
            $"WHERE \"rtid\" = '{meter}' AND \"{archive.TimeColumn}\" = '{Start.AddMinutes(15 * slot):yyyy-MM-ddTHH:mm:ss}Z'");
        return Assert.Single(rows);
    }

    private static void AssertRow(Dictionary<string, object?> row, double? reading, double? voltage, int? quality)
    {
        Assert.Equal(reading, row["meterreading"] is null ? null : Convert.ToDouble(row["meterreading"]));
        Assert.Equal(voltage, row["voltage"] is null ? null : Convert.ToDouble(row["voltage"]));
        Assert.Equal(quality, row["quality"] is null ? null : Convert.ToInt32(row["quality"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAutomatedPartialRedelivery_KeepsTheColumnsItDidNotSend(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Full(100, 230.5, 1)));

        var result = await WriteAsync(archive, new Delivery(meter, 0, new() { ["MeterReading"] = 150d }));

        Assert.Equal(1, result.Stored);
        AssertRow(await RowAsync(archive, meter), 150, 230.5, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AColumnSentAsExplicitNull_IsCleared_AndTheOthersStay(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Full(100, 230.5, 1)));

        await WriteAsync(archive, new Delivery(meter, 0, new() { ["Voltage"] = null }));

        AssertRow(await RowAsync(archive, meter), 100, null, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewPartialRow_StoresNullForTheColumnsItDidNotSend(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();

        var result = await WriteAsync(archive, new Delivery(meter, 0, new() { ["Voltage"] = 231d }));

        Assert.Equal(1, result.Stored);
        AssertRow(await RowAsync(archive, meter), null, 231, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchOfPointsThatNameDifferentColumns_StoresEveryRowCorrectly(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var a = OctoObjectId.GenerateNewId();
        var b = OctoObjectId.GenerateNewId();
        var c = OctoObjectId.GenerateNewId();
        var d = OctoObjectId.GenerateNewId();
        await WriteAsync(archive,
            new Delivery(a, 0, Full(100, 230, 1)),
            new Delivery(b, 0, Full(200, 231, 2)),
            new Delivery(c, 0, Full(300, 232, 3)));

        var result = await WriteAsync(archive,
            new Delivery(a, 0, new() { ["MeterReading"] = 101d }),
            new Delivery(b, 0, new() { ["Voltage"] = 241d }),
            new Delivery(c, 0, new() { ["Quality"] = null }),
            new Delivery(d, 0, new() { ["MeterReading"] = 400d }),
            new Delivery(a, 1, Full(110, 233, 1)),
            // The same row as the first point, later in the batch, with another column set: both apply, in order.
            new Delivery(a, 0, new() { ["MeterReading"] = 102d, ["Voltage"] = 250d }),
            new Delivery(b, 1, new() { ["MeterReading"] = 210d }));

        Assert.Equal(7, result.Stored);
        Assert.Empty(result.Skipped);
        AssertRow(await RowAsync(archive, a), 102, 250, 1);
        AssertRow(await RowAsync(archive, a, 1), 110, 233, 1);
        AssertRow(await RowAsync(archive, b), 200, 241, 2);
        AssertRow(await RowAsync(archive, b, 1), 210, null, null);
        AssertRow(await RowAsync(archive, c), 300, 232, null);
        AssertRow(await RowAsync(archive, d), 400, null, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoredManualRow_IsNotChangedByAnAutomatedPartialDelivery(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Full(100, 230.5, 1), ArchiveRowOrigin.Manual));

        var result = await WriteAsync(archive, new Delivery(meter, 0, new() { ["MeterReading"] = 150d }));

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        var row = await RowAsync(archive, meter);
        AssertRow(row, 100, 230.5, 1);
        Assert.Equal("manual", row["row_origin"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APrecedenceKeyTheDeliveryDoesNotName_KeepsItsStoredValue_AndCountsAsEqualForTheGuard(bool timeRange)
    {
        // AB#8622: an unsent ranking column keeps its stored value and the guard compares the point against the merged
        // row (COALESCE(EXCLUDED."quality", "quality")): equal on the last key, so the later point wins and is reported
        // as stored; the quality stays. A point that sends a worse quality loses, a better one wins.
        var archive = await CreateArchiveAsync(timeRange, [new ArchiveConflictKey("Quality", ConflictKeyOrder.LowerWins)]);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Full(100, 230.5, 2)));

        var unsent = await WriteAsync(archive, new Delivery(meter, 0, new() { ["MeterReading"] = 150d }));
        Assert.Equal(1, unsent.Stored);
        Assert.Empty(unsent.Skipped);
        AssertRow(await RowAsync(archive, meter), 150, 230.5, 2);

        var worse = await WriteAsync(archive, new Delivery(meter, 0, new() { ["MeterReading"] = 155d, ["Quality"] = 3 }));
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(worse.Skipped).Reason);
        AssertRow(await RowAsync(archive, meter), 150, 230.5, 2);

        var won = await WriteAsync(archive, new Delivery(meter, 0, new() { ["MeterReading"] = 160d, ["Quality"] = 1 }));
        Assert.Equal(1, won.Stored);
        AssertRow(await RowAsync(archive, meter), 160, 230.5, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APrecedenceKeySentAsExplicitNull_RefusesTheWholeBatch_AndWritesNothing(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, [new ArchiveConflictKey("Quality", ConflictKeyOrder.LowerWins)]);
        var meter = OctoObjectId.GenerateNewId();
        var other = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Full(100, 230.5, 2)));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(archive,
            new Delivery(other, 0, Full(500, 231, 1)),
            new Delivery(meter, 0, new() { ["MeterReading"] = 150d, ["Quality"] = null })));

        Assert.Contains("sends null for 'Quality'", error.Message);
        AssertRow(await RowAsync(archive, meter), 100, 230.5, 2);
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        Assert.Empty(await fixture.QueryAsync($"SELECT \"rtid\" FROM {archive.Table} WHERE \"rtid\" = '{other}'"));
    }

    [Fact]
    public async Task AMixedTimeRangeBatch_WritesWasUpdatedExplicitly_ForNewAndUpdatedRows()
    {
        // AB#6647: no row of a mixed multi-row upsert may rely on the DEFAULT of was_updated. Every column set's
        // statement names it with FALSE for a new row and sets TRUE on a conflict.
        var archive = await CreateArchiveAsync(timeRange: true);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, 0, Full(100, 230, 1)));

        await WriteAsync(archive,
            new Delivery(meter, 0, new() { ["Voltage"] = 231d }),
            new Delivery(meter, 1, new() { ["MeterReading"] = 101d }),
            new Delivery(meter, 2, Full(102, 232, 1)));

        var rows = await fixture.QueryAsync(
            $"SELECT \"was_updated\" FROM {archive.Table} WHERE \"rtid\" = '{meter}' ORDER BY \"window_start\"");
        Assert.Equal([true, false, false], rows.Select(r => (bool?)r["was_updated"]));
    }
}
