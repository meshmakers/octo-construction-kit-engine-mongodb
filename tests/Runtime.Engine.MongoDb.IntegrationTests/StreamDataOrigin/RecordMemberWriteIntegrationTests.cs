using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#8666 against a real CrateDB, through the repository, for raw and time-range archives: a point that carries a
///     record value (what a pipeline delivers: <c>NamePlate</c> → <see cref="RtRecord" />) writes its members into the
///     columns on the record's sub-paths. A member with a value is written, a member set to <c>null</c> clears its column,
///     a member the record does not contain keeps the stored value (AB#8622). Before, the raw path dropped every member,
///     so a pipeline write left whatever a REST insert (dotted keys) had stored. The REST shape, computed columns that read
///     a record member, a ranking column on a record member and manual protection are covered as well.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RecordMemberWriteIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly RtCkId<CkRecordId> NamePlateRecord = new("Test", new CkRecordId("NamePlate"));
    private static readonly RtCkId<CkRecordId> ReadingRecord = new("Test", new CkRecordId("Reading"));
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formulas =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Power", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("NamePlate.SerialNumber", new CrateColumnType.Primitive("TEXT"), false, false),
        new("NamePlate.ManufacturerName", new CrateColumnType.Primitive("TEXT"), false, false),
        new("Reading.Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Reading.Rank", new CrateColumnType.Primitive("INTEGER"), false, false),
        new("Scaled", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkArchiveColumnSpec[] Columns =
    [
        new("Power", false, false),
        new("NamePlate.SerialNumber", false, false),
        new("NamePlate.ManufacturerName", false, false),
        new("Reading.Value", false, false),
        new("Reading.Rank", false, false),
        new(string.Empty, false, false) { Name = "Scaled", Formula = "readingvalue * 2", ResultType = FormulaResultType.Double }
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
        // The stored formula is physical (AB#4779): the record member Reading.Value is the column readingvalue.
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(Snapshot(Columns, timeRange, precedence, rtId));
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, Formulas);
        var archive = new Archive(tenant, rtId, timeRange, repository);
        await fixture.SqlAsync(timeRange
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(archive.Table, ColumnDdl, 1, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(archive.Table, ColumnDdl, 1, 0));
        return archive;
    }

    private static ArchiveSnapshot Snapshot(IReadOnlyList<CkArchiveColumnSpec> columns, bool timeRange,
        IReadOnlyList<ArchiveConflictKey>? precedence, OctoObjectId rtId) =>
        new(rtId, Type, CkArchiveStatus.Activated, "meters", columns.ToArray())
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null,
            ConflictPrecedence = precedence ?? []
        };

    private sealed record Delivery(OctoObjectId Meter, Dictionary<string, object?> Attributes,
        ArchiveRowOrigin Origin = ArchiveRowOrigin.Automated);

    private static RtRecord NamePlate(Dictionary<string, object?> members) => new(NamePlateRecord, members);

    private static RtRecord Reading(Dictionary<string, object?> members) => new(ReadingRecord, members);

    /// <summary>The REST shape: dotted column paths, no record values (asset repository <c>insert</c>).</summary>
    private static Dictionary<string, object?> RestSeed() => new()
    {
        ["Power"] = 1d,
        ["NamePlate.SerialNumber"] = "SN-REST",
        ["NamePlate.ManufacturerName"] = "ACME"
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
                From = Start,
                To = Start.AddMinutes(15),
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
                Timestamp = Start,
                Attributes = d.Attributes,
                Origin = d.Origin,
                ManualWrittenBy = d.Origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
            }).ToArray());
        }

        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        return result;
    }

    private async Task<Dictionary<string, object?>> RowAsync(Archive archive, OctoObjectId meter)
    {
        var rows = await fixture.QueryAsync(
            $"SELECT \"power\", \"nameplateserialnumber\", \"nameplatemanufacturername\", \"readingvalue\", \"readingrank\", " +
            $"\"scaled\", \"row_origin\" FROM {archive.Table} " +
            $"WHERE \"rtid\" = '{meter}' AND \"{archive.TimeColumn}\" = '{Start:yyyy-MM-ddTHH:mm:ss}Z'");
        return Assert.Single(rows);
    }

    private static double? D(object? value) => value is null ? null : Convert.ToDouble(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecordMemberWithAValue_IsWritten_OverAValueTheRestShapeStored(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, RestSeed()));
        Assert.Equal("SN-REST", (await RowAsync(archive, meter))["nameplateserialnumber"]);

        var result = await WriteAsync(archive, new Delivery(meter, new()
        {
            ["Power"] = 3d,
            ["NamePlate"] = NamePlate(new() { ["SerialNumber"] = "SN-PIPE" })
        }));

        Assert.Equal(1, result.Stored);
        var row = await RowAsync(archive, meter);
        Assert.Equal(3d, D(row["power"]));
        Assert.Equal("SN-PIPE", row["nameplateserialnumber"]);
        // A member the record does not contain keeps the stored value (AB#8622).
        Assert.Equal("ACME", row["nameplatemanufacturername"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecordMemberSetToNull_ClearsItsColumn_AndTheOtherMembersStay(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, RestSeed()));

        await WriteAsync(archive, new Delivery(meter, new()
        {
            ["Power"] = 2d,
            ["NamePlate"] = NamePlate(new() { ["SerialNumber"] = null })
        }));

        var row = await RowAsync(archive, meter);
        Assert.Equal(2d, D(row["power"]));
        Assert.Null(row["nameplateserialnumber"]);
        Assert.Equal("ACME", row["nameplatemanufacturername"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecordWithoutAMember_OrNoRecordAtAll_KeepsTheStoredMembers(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, RestSeed()));

        await WriteAsync(archive, new Delivery(meter, new()
        {
            ["NamePlate"] = NamePlate(new() { ["ManufacturerName"] = "ACME 2" })
        }));
        await WriteAsync(archive, new Delivery(meter, new() { ["Power"] = 4d }));

        var row = await RowAsync(archive, meter);
        Assert.Equal(4d, D(row["power"]));
        Assert.Equal("SN-REST", row["nameplateserialnumber"]);
        Assert.Equal("ACME 2", row["nameplatemanufacturername"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewRowFromARecord_StoresItsMembers_AndNullForTheRest(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();

        var result = await WriteAsync(archive, new Delivery(meter, new()
        {
            ["NamePlate"] = NamePlate(new() { ["SerialNumber"] = "SN-NEW" })
        }));

        Assert.Equal(1, result.Stored);
        var row = await RowAsync(archive, meter);
        Assert.Equal("SN-NEW", row["nameplateserialnumber"]);
        Assert.Null(row["nameplatemanufacturername"]);
        Assert.Null(row["power"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AComputedColumnThatReadsARecordMember_IsEvaluatedFromTheRecord_AndOnTheMergedRow(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();

        await WriteAsync(archive, new Delivery(meter, new() { ["Reading"] = Reading(new() { ["Value"] = 5d }) }));
        Assert.Equal(10d, D((await RowAsync(archive, meter))["scaled"]));

        // A record that does not name the input leaves the computed column untouched (AB#8622).
        await WriteAsync(archive, new Delivery(meter, new() { ["Reading"] = Reading(new() { ["Rank"] = 3 }) }));
        var row = await RowAsync(archive, meter);
        Assert.Equal(5d, D(row["readingvalue"]));
        Assert.Equal(10d, D(row["scaled"]));

        await WriteAsync(archive, new Delivery(meter, new() { ["Reading"] = Reading(new() { ["Value"] = 7d }) }));
        Assert.Equal(14d, D((await RowAsync(archive, meter))["scaled"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARankingColumnOnARecordMember_IsCompared_AndAMissingMemberRanksWithTheStoredValue(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, [new ArchiveConflictKey("Reading.Rank", ConflictKeyOrder.LowerWins)]);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, new() { ["Reading"] = Reading(new() { ["Value"] = 5d, ["Rank"] = 1 }) }));

        // A worse rank inside the record loses: the record member is the ranking column.
        var worse = await WriteAsync(archive, new Delivery(meter, new()
        {
            ["Reading"] = Reading(new() { ["Value"] = 9d, ["Rank"] = 2 })
        }));
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(worse.Skipped).Reason);
        Assert.Equal(5d, D((await RowAsync(archive, meter))["readingvalue"]));

        // Without the rank member the stored rank is compared (a tie on the last key: stored), and it stays.
        var unranked = await WriteAsync(archive, new Delivery(meter, new()
        {
            ["Reading"] = Reading(new() { ["Value"] = 6d })
        }));
        Assert.Equal(1, unranked.Stored);
        var row = await RowAsync(archive, meter);
        Assert.Equal(6d, D(row["readingvalue"]));
        Assert.Equal(1, Convert.ToInt32(row["readingrank"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANullRankingMemberInsideARecord_RefusesTheBatch_AndNothingIsWritten(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, [new ArchiveConflictKey("Reading.Rank", ConflictKeyOrder.LowerWins)]);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, new() { ["Reading"] = Reading(new() { ["Value"] = 5d, ["Rank"] = 1 }) }));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(archive, new Delivery(meter, new()
        {
            ["Reading"] = Reading(new() { ["Value"] = 9d, ["Rank"] = null })
        })));

        Assert.Contains("sends null for 'Reading.Rank'", error.Message);
        var row = await RowAsync(archive, meter);
        Assert.Equal(5d, D(row["readingvalue"]));
        Assert.Equal(1, Convert.ToInt32(row["readingrank"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManualRowWrittenFromARecord_IsProtectedAgainstAnAutomatedRecordWrite(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, new Delivery(meter, new()
        {
            ["Power"] = 1d,
            ["NamePlate"] = NamePlate(new() { ["SerialNumber"] = "SN-MANUAL", ["ManufacturerName"] = "ACME" }),
            ["Reading"] = Reading(new() { ["Value"] = 1d, ["Rank"] = 1 })
        }, ArchiveRowOrigin.Manual));

        var result = await WriteAsync(archive, new Delivery(meter, new()
        {
            ["NamePlate"] = NamePlate(new() { ["SerialNumber"] = "SN-PIPE" })
        }));

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        var row = await RowAsync(archive, meter);
        Assert.Equal("SN-MANUAL", row["nameplateserialnumber"]);
        Assert.Equal("manual", row["row_origin"]);
    }
}
