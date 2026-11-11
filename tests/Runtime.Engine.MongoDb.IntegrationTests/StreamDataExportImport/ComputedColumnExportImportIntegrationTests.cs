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

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataExportImport;

/// <summary>
///     AB#8737 against a real CrateDB, for raw and time-range archives: an archive with a computed column
///     (Net = Consumption - Production) is exported and imported into a second archive of the same definition. Before,
///     the import failed with "Path must not be empty" on the computed column. The import evaluates the computed column
///     again from the imported row; a value a row carries for it is ignored (decision of AB#8737).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ComputedColumnExportImportIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formulas =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Consumption", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Production", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Net", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkArchiveColumnSpec[] Columns =
    [
        new("Consumption", false, false),
        new("Production", false, false),
        new(string.Empty, false, false) { Name = "Net", Formula = "consumption - production", ResultType = FormulaResultType.Double }
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Archive(string Tenant, OctoObjectId RtId, bool TimeRange, CrateDbStreamDataRepository Repository)
    {
        public string Table => TenantSchema.QualifiedArchiveTable(Tenant, RtId.ToString());
        public string TimeColumn => TimeRange ? "window_start" : "timestamp";
    }

    private async Task<Archive> CreateArchiveAsync(bool timeRange)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var snapshot = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "measurements", Columns)
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null
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

    private async Task WriteAsync(Archive archive, OctoObjectId meter, int slot, double consumption, double production)
    {
        var attributes = new Dictionary<string, object?> { ["Consumption"] = consumption, ["Production"] = production };
        if (archive.TimeRange)
        {
            await archive.Repository.InsertTimeRangeAsync(archive.RtId,
            [
                new TimeRangeStreamDataPoint
                {
                    RtId = meter, CkTypeId = Type, From = Start.AddMinutes(15 * slot),
                    To = Start.AddMinutes(15 * (slot + 1)), Attributes = attributes
                }
            ], Ct);
        }
        else
        {
            await archive.Repository.InsertAsync(archive.RtId,
            [
                new StreamDataPoint { RtId = meter, CkTypeId = Type, Timestamp = Start.AddMinutes(15 * slot), Attributes = attributes }
            ]);
        }

        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
    }

    private async Task<List<IReadOnlyDictionary<string, object?>>> ExportAsync(Archive archive)
    {
        var exported = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in archive.Repository.ExportRowsAsync(archive.RtId, null, Ct))
        {
            exported.Add(row);
        }

        return exported;
    }

    private async Task<List<(double? Consumption, double? Production, double? Net)>> RowsAsync(Archive archive)
    {
        var rows = await fixture.QueryAsync(
            $"SELECT \"consumption\", \"production\", \"net\" FROM {archive.Table} ORDER BY \"{archive.TimeColumn}\"");
        return rows.Select(r => (D(r["consumption"]), D(r["production"]), D(r["net"]))).ToList();

        static double? D(object? v) => v is null ? null : Convert.ToDouble(v);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExportWithAComputedColumn_ImportsAgain(bool timeRange)
    {
        var source = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(source, meter, 0, 10, 4);
        await WriteAsync(source, meter, 1, 7, 9);

        var exported = await ExportAsync(source);
        Assert.Equal(2, exported.Count);
        Assert.All(exported, row => Assert.True(row.ContainsKey("net")));

        var target = await CreateArchiveAsync(timeRange);
        await target.Repository.ImportRowsAsync(target.RtId, exported.ToAsyncEnumerable(),
            timeRange ? ArchiveImportMode.Upsert : ArchiveImportMode.InsertOnly, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {target.Table}");

        Assert.Equal(await RowsAsync(source), await RowsAsync(target));
        Assert.Equal([(10d, 4d, 6d), (7d, 9d, -2d)], await RowsAsync(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AValueForTheComputedColumnInARow_IsIgnoredAndEvaluatedAgain(bool timeRange)
    {
        var source = await CreateArchiveAsync(timeRange);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(source, meter, 0, 10, 4);

        var forged = (await ExportAsync(source))
            .Select(row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(row) { ["net"] = 999d })
            .ToList();

        var target = await CreateArchiveAsync(timeRange);
        await target.Repository.ImportRowsAsync(target.RtId, forged.ToAsyncEnumerable(),
            timeRange ? ArchiveImportMode.Upsert : ArchiveImportMode.InsertOnly, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {target.Table}");

        Assert.Equal([(10d, 4d, 6d)], await RowsAsync(target));
    }
}
