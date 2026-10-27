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
///     AB#5356 against a real CrateDB: every raw and time-range row records its origin (manual / automated), and an
///     archive that ranks by the reserved key <c>$Origin</c> keeps a manual value permanently — an automated delivery
///     never replaces it, whatever its quality — while another manual write always does. The archive here has the shape
///     of the energy community measurements archive: <c>[$Origin, DataQuality LowerWins, SourceDocumentDate HigherWins]</c>.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveRowOriginIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId Meter = OctoObjectId.GenerateNewId();
    private static readonly DateTime Window = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveConflictKey[] EnergyCommunityPrecedence =
    [
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
        new("DataQuality", ConflictKeyOrder.LowerWins),
        new("SourceDocumentDate", ConflictKeyOrder.HigherWins)
    ];

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("DataQuality", new CrateColumnType.Primitive("INTEGER"), false, false),
        new("SourceDocumentDate", new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE"), false, false)
    ];

    private sealed record Archive(string Tenant, OctoObjectId RtId, CrateDbStreamDataRepository Repository)
    {
        public string Table => TenantSchema.QualifiedArchiveTable(Tenant, RtId.ToString());
    }

    private Archive NewTimeRangeArchive(IReadOnlyList<ArchiveConflictKey> precedence, string? tenant = null, OctoObjectId? rtId = null)
    {
        tenant ??= CrateStagingCopyFixture.NewTenantId();
        var archiveRtId = rtId ?? OctoObjectId.GenerateNewId();
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(archiveRtId)).Returns(new ArchiveSnapshot(archiveRtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15),
            ConflictPrecedence = precedence
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>());
        return new Archive(tenant, archiveRtId, repository);
    }

    private async Task<Archive> CreateTimeRangeArchiveAsync(IReadOnlyList<ArchiveConflictKey> precedence)
    {
        var archive = NewTimeRangeArchive(precedence);
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(archive.Table, ColumnDdl, 1, 0));
        return archive;
    }

    private static TimeRangeStreamDataPoint Point(double value, int quality, DateTime documentDate,
        ArchiveRowOrigin origin = ArchiveRowOrigin.Automated) => new()
    {
        RtId = Meter,
        CkTypeId = Type,
        From = Window,
        To = Window.AddMinutes(15),
        Attributes = new Dictionary<string, object?>
        {
            ["Value"] = value,
            ["DataQuality"] = quality,
            ["SourceDocumentDate"] = documentDate
        },
        Origin = origin
    };

    private async Task<Dictionary<string, object?>> ReadRowAsync(Archive archive)
    {
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        return (await fixture.QueryAsync(
            $"SELECT \"value\", \"dataquality\", \"row_origin\" FROM {archive.Table} WHERE \"rtid\" = '{Meter}'")).Single();
    }

    private async Task WriteAsync(Archive archive, TimeRangeStreamDataPoint point)
    {
        await archive.Repository.InsertTimeRangeAsync(archive.RtId, [point], TestContext.Current.CancellationToken);
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
    }

    [Fact]
    public async Task AWriteThatDeclaresNothing_IsStoredAsAutomated()
    {
        var archive = await CreateTimeRangeArchiveAsync([]);

        await WriteAsync(archive, Point(10, 1, Window));

        Assert.Equal("automated", (await ReadRowAsync(archive))["row_origin"]);
    }

    [Fact]
    public async Task AManualCorrection_ReplacesADeliveredMeasurement_WhateverItsQuality()
    {
        var archive = await CreateTimeRangeArchiveAsync(EnergyCommunityPrecedence);
        await WriteAsync(archive, Point(10, 1, Window));            // automated L1 measurement

        await WriteAsync(archive, Point(12, 3, Window.AddDays(-1), ArchiveRowOrigin.Manual)); // manual, states L3, older date

        var row = await ReadRowAsync(archive);
        Assert.Equal(12d, row["value"]);
        Assert.Equal(3, row["dataquality"]);
        Assert.Equal("manual", row["row_origin"]);
    }

    [Fact]
    public async Task AManualValue_IsNeverReplacedByAnAutomatedDelivery_WhateverItsQuality()
    {
        var archive = await CreateTimeRangeArchiveAsync(EnergyCommunityPrecedence);
        await WriteAsync(archive, Point(12, 3, Window, ArchiveRowOrigin.Manual));

        await WriteAsync(archive, Point(10, 1, Window.AddDays(1)));  // better quality, newer document, automated

        var row = await ReadRowAsync(archive);
        Assert.Equal(12d, row["value"]);
        Assert.Equal("manual", row["row_origin"]);
    }

    [Fact]
    public async Task AWrongManualValue_IsFixedWithAnotherManualWrite_EvenWithAWorseQuality()
    {
        var archive = await CreateTimeRangeArchiveAsync(EnergyCommunityPrecedence);
        await WriteAsync(archive, Point(99, 1, Window, ArchiveRowOrigin.Manual));

        await WriteAsync(archive, Point(12, 2, Window.AddDays(-5), ArchiveRowOrigin.Manual));

        var row = await ReadRowAsync(archive);
        Assert.Equal(12d, row["value"]);
        Assert.Equal(2, row["dataquality"]);
    }

    [Fact]
    public async Task BetweenAutomatedDeliveries_TheQualityStillDecides()
    {
        var archive = await CreateTimeRangeArchiveAsync(EnergyCommunityPrecedence);
        await WriteAsync(archive, Point(10, 1, Window));

        await WriteAsync(archive, Point(11, 3, Window.AddDays(1)));  // estimate after a measurement

        Assert.Equal(10d, (await ReadRowAsync(archive))["value"]);
    }

    [Fact]
    public async Task AnArchiveThatDoesNotRankByOrigin_BehavesAsBefore_TheLastWriteWins()
    {
        var archive = await CreateTimeRangeArchiveAsync([]);
        await WriteAsync(archive, Point(12, 3, Window, ArchiveRowOrigin.Manual));

        await WriteAsync(archive, Point(10, 1, Window));

        var row = await ReadRowAsync(archive);
        Assert.Equal(10d, row["value"]);
        Assert.Equal("automated", row["row_origin"]);
    }

    [Fact]
    public async Task AMultiRowBatch_RecordsTheOriginOfEveryRow()
    {
        var archive = await CreateTimeRangeArchiveAsync([]);
        var manual = Point(1, 1, Window, ArchiveRowOrigin.Manual);
        var automated = new TimeRangeStreamDataPoint
        {
            RtId = Meter, CkTypeId = Type, From = Window.AddMinutes(15), To = Window.AddMinutes(30),
            Attributes = new Dictionary<string, object?> { ["Value"] = 2d }
        };

        await archive.Repository.InsertTimeRangeAsync(archive.RtId, [manual, automated], TestContext.Current.CancellationToken);
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");

        var rows = await fixture.QueryAsync($"SELECT \"value\", \"row_origin\" FROM {archive.Table} ORDER BY \"window_start\"");
        Assert.Equal(["manual", "automated"], rows.Select(r => (string?)r["row_origin"]));
    }

    [Fact]
    public async Task ATableProvisionedBeforeTheOrigin_GetsTheColumnOnTheFirstWrite_AndItsRowsReadAsAutomated()
    {
        var archive = NewTimeRangeArchive(EnergyCommunityPrecedence);
        // The windowed table as it was provisioned before AB#5356: no row_origin column.
        await fixture.SqlAsync(
            $"CREATE TABLE {archive.Table} (\"window_start\" TIMESTAMP WITH TIME ZONE NOT NULL, \"window_end\" TIMESTAMP WITH TIME ZONE NOT NULL, " +
            "\"rtid\" TEXT NOT NULL, \"cktypeid\" TEXT NOT NULL, \"rtcreationdatetime\" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP, " +
            "\"rtchangeddatetime\" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP, \"rtwellknownname\" TEXT, \"was_updated\" BOOLEAN NOT NULL DEFAULT FALSE, " +
            "\"value\" DOUBLE PRECISION, \"dataquality\" INTEGER, \"sourcedocumentdate\" TIMESTAMP WITH TIME ZONE, " +
            "PRIMARY KEY (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\")) CLUSTERED INTO 1 SHARDS WITH (number_of_replicas = 0)");
        await fixture.SqlAsync(
            $"INSERT INTO {archive.Table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value\", \"dataquality\") " +
            $"VALUES ('{Window:O}', '{Window.AddMinutes(15):O}', '{Meter}', '{Type}', FALSE, 10, 1)");
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");

        await WriteAsync(archive, Point(12, 3, Window, ArchiveRowOrigin.Manual));

        var row = await ReadRowAsync(archive);
        Assert.Equal(12d, row["value"]);   // the old row had no origin, so it counted as automated and lost
        Assert.Equal("manual", row["row_origin"]);
    }

    [Fact]
    public async Task ARawArchive_RecordsTheOriginToo()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archiveRtId = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archiveRtId);
        var repository = fixture.NewRepository(tenant, archiveRtId);
        var table = TenantSchema.QualifiedArchiveTable(tenant, archiveRtId.ToString());

        await repository.InsertAsync(archiveRtId, new StreamDataPoint
        {
            RtId = Meter,
            CkTypeId = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")),
            Timestamp = Window,
            Attributes = new Dictionary<string, object?> { ["Value"] = 1.5 },
            Origin = ArchiveRowOrigin.Manual
        });
        await fixture.SqlAsync($"REFRESH TABLE {table}");

        Assert.Equal("manual", (await fixture.QueryAsync($"SELECT \"row_origin\" FROM {table}")).Single()["row_origin"]);
    }
}
