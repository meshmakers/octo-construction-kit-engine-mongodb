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
///     AB#8622 together with the configured origin precedence (AB#5356, AB#7656), against a real CrateDB, for raw and
///     time-range archives. A partial delivery (one statement per column set), an explicit null and the computed column
///     evaluated on the merged row (stored-row read) must honour where the archive puts <c>$Origin</c>:
///     ignored, after <c>DataQuality</c>, or not named (origin first). A manual value wins a conflict only when the
///     archive is not configured otherwise. Archive: Net = Consumption - Production, ranked by DataQuality (LowerWins)
///     and SourceDocumentDate (HigherWins).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class PartialDeliveryOriginPrecedenceIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldDocument = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewDocument = new(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formulas =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Consumption", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Production", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("DataQuality", new CrateColumnType.Primitive("INTEGER"), false, false),
        new("SourceDocumentDate", new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE"), false, false),
        new("Net", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkArchiveColumnSpec[] Columns =
    [
        new("Consumption", false, false),
        new("Production", false, false),
        new("DataQuality", false, false),
        new("SourceDocumentDate", false, false),
        new(string.Empty, false, false) { Name = "Net", Formula = "consumption - production", ResultType = FormulaResultType.Double }
    ];

    private static readonly ArchiveConflictKey Quality = new("DataQuality", ConflictKeyOrder.LowerWins);
    private static readonly ArchiveConflictKey Recency = new("SourceDocumentDate", ConflictKeyOrder.HigherWins);

    /// <summary><c>$Origin</c> opted out: recency alone decides, also over a manual row.</summary>
    private static readonly ArchiveConflictKey[] OriginIgnored = [new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.Ignore), Recency];

    /// <summary><c>$Origin</c> after the quality: a better automated measurement beats a manual row.</summary>
    private static readonly ArchiveConflictKey[] OriginAfterQuality =
        [Quality, new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins), Recency];

    /// <summary><c>$Origin</c> not named: the archive ranks by origin first (AB#7656 default).</summary>
    private static readonly ArchiveConflictKey[] OriginNotNamed = [Quality, Recency];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Archive(string Tenant, OctoObjectId RtId, bool TimeRange, CrateDbStreamDataRepository Repository)
    {
        public string Table => TenantSchema.QualifiedArchiveTable(Tenant, RtId.ToString());
        public string TimeColumn => TimeRange ? "window_start" : "timestamp";
    }

    private async Task<Archive> CreateArchiveAsync(bool timeRange, IReadOnlyList<ArchiveConflictKey> precedence)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var snapshot = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "measurements", Columns)
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null,
            ConflictPrecedence = precedence
        };
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(snapshot);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, Formulas);
        var archive = new Archive(tenant, rtId, timeRange, repository);
        await fixture.SqlAsync(timeRange
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(archive.Table, ColumnDdl, 1, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(archive.Table, ColumnDdl, 1, 0));
        return archive;
    }

    private sealed record Delivery(OctoObjectId Meter, Dictionary<string, object?> Attributes,
        ArchiveRowOrigin Origin = ArchiveRowOrigin.Automated, int Slot = 0);

    private static Dictionary<string, object?> Full(double? consumption, double? production, int quality, DateTime document) => new()
    {
        ["Consumption"] = consumption,
        ["Production"] = production,
        ["DataQuality"] = quality,
        ["SourceDocumentDate"] = document
    };

    private static Delivery Manual(OctoObjectId meter, Dictionary<string, object?> attributes, int slot = 0) =>
        new(meter, attributes, ArchiveRowOrigin.Manual, slot);

    private async Task<StreamDataWriteResult> WriteAsync(Archive archive, params Delivery[] deliveries)
    {
        try
        {
            if (archive.TimeRange)
            {
                return await archive.Repository.InsertTimeRangeAsync(archive.RtId, deliveries.Select(d => new TimeRangeStreamDataPoint
                {
                    RtId = d.Meter,
                    CkTypeId = Type,
                    From = Start.AddMinutes(15 * d.Slot),
                    To = Start.AddMinutes(15 * (d.Slot + 1)),
                    Attributes = d.Attributes,
                    Origin = d.Origin,
                    ManualWrittenBy = d.Origin == ArchiveRowOrigin.Manual ? "jane.doe" : null,
                    ManualReason = d.Origin == ArchiveRowOrigin.Manual ? "meter exchanged" : null
                }).ToArray(), Ct);
            }

            return await archive.Repository.InsertAsync(archive.RtId, deliveries.Select(d => new StreamDataPoint
            {
                RtId = d.Meter,
                CkTypeId = Type,
                Timestamp = Start.AddMinutes(15 * d.Slot),
                Attributes = d.Attributes,
                Origin = d.Origin,
                ManualWrittenBy = d.Origin == ArchiveRowOrigin.Manual ? "jane.doe" : null,
                ManualReason = d.Origin == ArchiveRowOrigin.Manual ? "meter exchanged" : null
            }).ToArray());
        }
        finally
        {
            await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        }
    }

    private sealed record Row(double? Consumption, double? Production, int? Quality, double? Net, string? Origin,
        string? WrittenBy, string? Reason);

    private async Task<Row> RowAsync(Archive archive, OctoObjectId meter, int slot = 0)
    {
        var rows = await fixture.QueryAsync(
            $"SELECT \"consumption\", \"production\", \"dataquality\", \"net\", \"row_origin\", \"manual_written_by\", \"manual_reason\" " +
            $"FROM {archive.Table} " +
            $"WHERE \"rtid\" = '{meter}' AND \"{archive.TimeColumn}\" = '{Start.AddMinutes(15 * slot):yyyy-MM-ddTHH:mm:ss}Z'");
        var row = Assert.Single(rows);
        return new Row(D(row["consumption"]), D(row["production"]), row["dataquality"] is null ? null : Convert.ToInt32(row["dataquality"]),
            D(row["net"]), row["row_origin"] as string, row["manual_written_by"] as string, row["manual_reason"] as string);

        static double? D(object? v) => v is null ? null : Convert.ToDouble(v);
    }

    /// <summary>
    ///     AB#8622 option C: a point that does not send every ranking column refuses the whole batch before the write.
    /// </summary>
    private async Task AssertRefusedAsync(Archive archive, string missing, params Delivery[] deliveries)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(archive, deliveries));
        Assert.Contains("nothing was written", error.Message);
        Assert.Contains($"does not send {missing}", error.Message);
    }

    private async Task<int> RowCountAsync(Archive archive)
    {
        var rows = await fixture.QueryAsync($"SELECT COUNT(*) AS n FROM {archive.Table}");
        return Convert.ToInt32(Assert.Single(rows)["n"]);
    }

    private static Row ManualRow(double consumption, double production, int quality) =>
        new(consumption, production, quality, consumption - production, "manual", "jane.doe", "meter exchanged");

    private static Row AutomatedRow(double? consumption, double? production, int? quality, double? net) =>
        new(consumption, production, quality, net, "automated", null, null);

    // 1. $Origin ignored: recency decides, a newer automated delivery replaces a stored manual row.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginIgnored_ANewerFullAutomatedDelivery_ReplacesAStoredManualRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginIgnored);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, OldDocument)));

        var result = await WriteAsync(archive, new Delivery(meter, Full(12, 5, 3, NewDocument)));

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        Assert.Equal(AutomatedRow(12, 5, 3, 7), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginIgnored_ANewerPartialAutomatedDelivery_ReplacesAStoredManualRow_OnTheMergedRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginIgnored);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, OldDocument)));

        var result = await WriteAsync(archive,
            new Delivery(meter, new() { ["Consumption"] = 12d, ["SourceDocumentDate"] = NewDocument }));

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        // Production and DataQuality were not sent and keep their stored values; Net = 12 - 4 on the merged row.
        Assert.Equal(AutomatedRow(12, 4, 2, 8), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginIgnored_AnOlderPartialAutomatedDelivery_LosesOnRecency_AndChangesNothing(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginIgnored);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, NewDocument)));

        var result = await WriteAsync(archive,
            new Delivery(meter, new() { ["Consumption"] = 12d, ["SourceDocumentDate"] = OldDocument }));

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(result.Skipped).Reason);
        Assert.Equal(ManualRow(10, 4, 2), await RowAsync(archive, meter));
    }

    // 2. $Origin after DataQuality: the quality decides before the origin.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_ABetterQualityFullAutomatedPoint_BeatsAManualRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, NewDocument)));

        var result = await WriteAsync(archive, new Delivery(meter, Full(12, 5, 1, OldDocument)));

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        Assert.Equal(AutomatedRow(12, 5, 1, 7), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_ABetterQualityPartialAutomatedPoint_BeatsAManualRow_OnTheMergedRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, OldDocument)));

        var result = await WriteAsync(archive,
            new Delivery(meter, new() { ["Consumption"] = 12d, ["DataQuality"] = 1, ["SourceDocumentDate"] = NewDocument }));

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        Assert.Equal(AutomatedRow(12, 4, 1, 8), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_ABetterQualityPartialPoint_ThatDoesNotSendTheRecencyKey_IsRefused(bool timeRange)
    {
        // AB#8622 option C: the point does not send SourceDocumentDate. It would win on the quality while the write
        // result compared the NULL it sent against the stored date it kept; it is refused before the write instead.
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, OldDocument)));

        await AssertRefusedAsync(archive, "'SourceDocumentDate'",
            new Delivery(meter, new() { ["Consumption"] = 12d, ["DataQuality"] = 1 }));

        Assert.Equal(ManualRow(10, 4, 2), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_AWorseQualityPartialAutomatedPoint_IsSkipped_AndNothingIsPartlyApplied(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 2, OldDocument)));

        var result = await WriteAsync(archive,
            new Delivery(meter, new() { ["Production"] = 1d, ["DataQuality"] = 3, ["SourceDocumentDate"] = NewDocument }));

        Assert.Equal(0, result.Stored);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(meter, skipped.RtId);
        Assert.Equal(ArchiveRowOrigin.Automated, skipped.Origin);
        // The stored row is manual and the archive ranks by origin: reported as the manual value's protection, also
        // though the quality decided before the origin was reached.
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, skipped.Reason);
        Assert.Equal(ManualRow(10, 4, 2), await RowAsync(archive, meter));
    }

    // 3. Default, $Origin not named: origin first, a manual value is never replaced by an automated delivery.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginNotNamed_ABetterNewerFullAutomatedDelivery_DoesNotReplaceAManualRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginNotNamed);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 3, OldDocument)));

        var result = await WriteAsync(archive, new Delivery(meter, Full(12, 5, 1, NewDocument)));

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        Assert.Equal(ManualRow(10, 4, 3), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginNotNamed_ABetterNewerPartialAutomatedDelivery_DoesNotReplaceAManualRow_NorItsComputedColumn(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginNotNamed);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, Manual(meter, Full(10, 4, 3, OldDocument)));

        var result = await WriteAsync(archive,
            new Delivery(meter, new() { ["Production"] = 1d, ["DataQuality"] = 1, ["SourceDocumentDate"] = NewDocument }));

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        Assert.Equal(ManualRow(10, 4, 3), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginNotNamed_AManualPartialCorrection_WithoutTheRecencyKey_IsRefused(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginNotNamed);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, Full(10, 4, 1, NewDocument)));

        await AssertRefusedAsync(archive, "'SourceDocumentDate'",
            Manual(meter, new() { ["Production"] = 3d, ["DataQuality"] = 3 }));

        Assert.Equal(AutomatedRow(10, 4, 1, 6), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginNotNamed_AManualPartialCorrection_ThatSendsEveryRankingColumn_ReplacesABetterAutomatedRow_OnTheMergedRow(bool timeRange)
    {
        // The partial point sends both ranking columns (and Production) but not Consumption: it wins on the origin, is
        // reported as stored, Consumption keeps its stored value and Net = 10 - 3 on the merged row.
        var archive = await CreateArchiveAsync(timeRange, OriginNotNamed);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, Full(10, 4, 1, NewDocument)));

        var result = await WriteAsync(archive,
            Manual(meter, new() { ["Production"] = 3d, ["DataQuality"] = 3, ["SourceDocumentDate"] = OldDocument }));

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        Assert.Equal(new Row(10, 3, 3, 7, "manual", "jane.doe", "meter exchanged"), await RowAsync(archive, meter));
    }

    // 4. A manual point that loses against a configured precedence fails the write (REST: 409 ManualWriteNotStored).

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_AManualPartialPointAgainstABetterQualityRow_IsNotStored_AndFailsTheWrite(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        var other = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, Full(10, 4, 1, OldDocument)));

        var ex = await Assert.ThrowsAsync<ManualStreamDataNotAppliedException>(() => WriteAsync(archive,
            Manual(meter, new() { ["Production"] = 3d, ["DataQuality"] = 3, ["SourceDocumentDate"] = NewDocument }),
            new Delivery(other, Full(20, 5, 1, OldDocument))));

        // asp-repo maps this exception to 409 ManualWriteNotStored with ex.Result as the body's write result.
        Assert.Equal(archive.RtId, ex.ArchiveRtId);
        Assert.Equal(1, ex.Result.Stored);
        var skipped = Assert.Single(ex.Result.Skipped);
        Assert.Equal(meter, skipped.RtId);
        Assert.Equal(ArchiveRowOrigin.Manual, skipped.Origin);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, skipped.Reason);
        Assert.Equal(Start, skipped.From);
        Assert.Equal(timeRange ? Start.AddMinutes(15) : null, skipped.To);
        Assert.Equal(AutomatedRow(10, 4, 1, 6), await RowAsync(archive, meter));
        Assert.Equal(AutomatedRow(20, 5, 1, 15), await RowAsync(archive, other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_AManualPartialPointWithAnExplicitNull_ThatLoses_ChangesNothing(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, Full(10, 4, 1, OldDocument)));

        await Assert.ThrowsAsync<ManualStreamDataNotAppliedException>(() => WriteAsync(archive,
            Manual(meter, new() { ["Production"] = null, ["DataQuality"] = 2, ["SourceDocumentDate"] = NewDocument })));

        Assert.Equal(AutomatedRow(10, 4, 1, 6), await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_AManualPartialPointOfEqualQuality_WithoutTheRecencyKey_IsRefused(bool timeRange)
    {
        // It would win on the origin and fail the write as "not stored" (REST 409) although the row was written.
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, Full(10, 4, 1, NewDocument)));

        await AssertRefusedAsync(archive, "'SourceDocumentDate'",
            Manual(meter, new() { ["Production"] = null, ["DataQuality"] = 1 }));

        Assert.Equal(AutomatedRow(10, 4, 1, 6), await RowAsync(archive, meter));
    }

    // 5. A mixed batch with different column sets: every row is decided on its own.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginAfterQuality_AMixedBatchWithDifferentColumnSets_DecidesEveryRowIndependently(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginAfterQuality);
        var a = OctoObjectId.GenerateNewId();
        var b = OctoObjectId.GenerateNewId();
        var c = OctoObjectId.GenerateNewId();
        var d = OctoObjectId.GenerateNewId();
        var e = OctoObjectId.GenerateNewId();
        await WriteAsync(archive,
            Manual(a, Full(10, 4, 2, OldDocument)),
            new Delivery(b, Full(20, 5, 2, OldDocument)),
            new Delivery(c, Full(30, 6, 1, OldDocument)),
            Manual(d, Full(40, 8, 3, OldDocument)));

        var result = await WriteAsync(archive,
            new Delivery(a, new() { ["Consumption"] = 11d, ["DataQuality"] = 1, ["SourceDocumentDate"] = NewDocument }),   // better than manual: wins
            new Delivery(b, new() { ["Production"] = 1d, ["DataQuality"] = 3, ["SourceDocumentDate"] = NewDocument }),     // worse: loses
            new Delivery(c, Full(33, 3, 1, NewDocument)),                                                                  // tie, newer: wins
            new Delivery(d, new() { ["Production"] = 2d, ["DataQuality"] = 3, ["SourceDocumentDate"] = NewDocument }),     // tie, manual stored: loses
            new Delivery(e, new() { ["Consumption"] = 5d, ["DataQuality"] = 2, ["SourceDocumentDate"] = NewDocument }));   // new row

        Assert.Equal(3, result.Stored);
        Assert.Collection(result.Skipped,
            s => Assert.Equal((b, StreamDataSkipReason.LowerPrecedence), (s.RtId, s.Reason)),
            s => Assert.Equal((d, StreamDataSkipReason.ManualValueProtected), (s.RtId, s.Reason)));
        Assert.Equal(AutomatedRow(11, 4, 1, 7), await RowAsync(archive, a));
        Assert.Equal(AutomatedRow(20, 5, 2, 15), await RowAsync(archive, b));
        Assert.Equal(AutomatedRow(33, 3, 1, 30), await RowAsync(archive, c));
        Assert.Equal(ManualRow(40, 8, 3), await RowAsync(archive, d));
        Assert.Equal(AutomatedRow(5, null, 2, null), await RowAsync(archive, e));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginNotNamed_AMixedBatchWithPointsWithoutRankingColumns_IsRefusedAsAWhole(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, OriginNotNamed);
        var a = OctoObjectId.GenerateNewId();
        var b = OctoObjectId.GenerateNewId();
        var c = OctoObjectId.GenerateNewId();
        var d = OctoObjectId.GenerateNewId();
        await WriteAsync(archive,
            Manual(a, Full(10, 4, 3, OldDocument)),
            new Delivery(b, Full(20, 5, 3, OldDocument)),
            new Delivery(c, Full(30, 6, 1, OldDocument)));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(archive,
            new Delivery(a, new() { ["Consumption"] = 11d, ["DataQuality"] = 1, ["SourceDocumentDate"] = NewDocument }),   // complete
            new Delivery(b, new() { ["Production"] = 1d, ["DataQuality"] = 1 }),                                           // no recency key
            Manual(c, new() { ["Consumption"] = 31d }),                                                                     // no ranking column
            new Delivery(d, Full(40, 8, 1, NewDocument))));                                                                // new row, complete

        Assert.Contains("2 point(s) do not, nothing was written", error.Message);
        Assert.Contains($"point 1 (rtId '{b}'", error.Message);
        Assert.Contains("does not send 'SourceDocumentDate'", error.Message);
        Assert.Contains($"point 2 (rtId '{c}'", error.Message);
        Assert.Contains("does not send 'DataQuality', 'SourceDocumentDate'", error.Message);
        // Nothing of the batch is stored, the complete points included.
        Assert.Equal(ManualRow(10, 4, 3), await RowAsync(archive, a));
        Assert.Equal(AutomatedRow(20, 5, 3, 15), await RowAsync(archive, b));
        Assert.Equal(AutomatedRow(30, 6, 1, 24), await RowAsync(archive, c));
        Assert.Equal(3, await RowCountAsync(archive));
    }
}
