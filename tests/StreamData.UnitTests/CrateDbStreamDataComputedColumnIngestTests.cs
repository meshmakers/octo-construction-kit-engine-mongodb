using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Ingest-time evaluation of computed columns (concept §5): a computed column's formula is
/// evaluated over the row's other column values and the result is written into the row under the
/// computed column's physical name.
/// </summary>
public class CrateDbStreamDataComputedColumnIngestTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("EnergyMeter"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, _db, _mgmt, Config, "tenant-x", _store, Formula);

    private static CkArchiveColumnSpec Ing(string path) => new(path, Indexed: true, Required: false);

    private static CkArchiveColumnSpec Comp(string name, string formula, FormulaResultType rt) =>
        new(string.Empty, Indexed: true, Required: false) { Name = name, Formula = formula, ResultType = rt };

    private void StubArchive(params CkArchiveColumnSpec[] columns) =>
        A.CallTo(() => _store.GetAsync(Archive)).Returns(
            new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, "energy", columns));

    private static StreamDataPoint Point(Dictionary<string, object?> attrs) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = SomeType,
        Timestamp = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
        Attributes = attrs,
    };

    private async Task<DataPointDto> CaptureInsertAsync(StreamDataPoint point)
    {
        DataPointDto? captured = null;
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<DataPointDto>._))
            .Invokes(call => captured = call.GetArgument<DataPointDto>(3))
            .Returns(Task.CompletedTask);

        await NewSut().InsertAsync(Archive, point);
        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task ComputedColumn_EvaluatedAndAddedToRow()
    {
        StubArchive(Ing("activePower"), Ing("apparentPower"),
            Comp("powerFactor", "activepower / apparentpower", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["activePower"] = 8.0, ["apparentPower"] = 10.0 }));

        Assert.Equal(0.8, Assert.IsType<double>(dto.Attributes["powerfactor"]), 10);
    }

    [Fact]
    public async Task ComputedColumn_DuringFormulaChange_DualWritesActiveAndPending()
    {
        // PendingFormula set ⇒ ingest writes the active formula into the base column (current readers)
        // and the pending formula into the versioned column (post-swap consistency) — AB#4189 §8 D-7.3.
        StubArchive(
            Ing("activePower"),
            new CkArchiveColumnSpec(string.Empty, Indexed: true, Required: false)
            {
                Name = "derived",
                Formula = "activepower * 2",
                ResultType = FormulaResultType.Double,
                ComputedState = ComputedColumnState.Active,
                ComputedVersion = 0,
                PendingFormula = "activepower * 3",
            });

        var dto = await CaptureInsertAsync(Point(new() { ["activePower"] = 10.0 }));

        Assert.Equal(20.0, Assert.IsType<double>(dto.Attributes["derived"]), 10);     // active -> base
        Assert.Equal(30.0, Assert.IsType<double>(dto.Attributes["derived__v1"]), 10); // pending -> __v1
    }

    [Fact]
    public async Task ComputedColumn_AppearsInUserColumnNames()
    {
        StubArchive(Ing("activePower"), Comp("derived", "activepower * 2", FormulaResultType.Double));

        IReadOnlyList<string>? columns = null;
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<DataPointDto>._))
            .Invokes(call => columns = call.GetArgument<IReadOnlyList<string>>(2))
            .Returns(Task.CompletedTask);

        await NewSut().InsertAsync(Archive, Point(new() { ["activePower"] = 5.0 }));

        Assert.Contains("derived", columns!);
    }

    [Fact]
    public async Task ComputedColumn_MissingOperand_StoresNull()
    {
        StubArchive(Ing("activePower"),
            Comp("ratio", "activepower / apparentpower", FormulaResultType.Double));

        // apparentpower is not supplied → unbound argument → NaN → NULL.
        var dto = await CaptureInsertAsync(Point(new() { ["activePower"] = 8.0 }));

        Assert.Null(dto.Attributes["ratio"]);
    }

    [Fact]
    public async Task ComputedColumn_Chained_EvaluatedInDependencyOrder()
    {
        // c2 references c1; declared out of order to exercise the topological sort.
        StubArchive(
            Ing("a"),
            Comp("c2", "c1 * 2", FormulaResultType.Double),
            Comp("c1", "a + 1", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["a"] = 3.0 }));

        Assert.Equal(4.0, Assert.IsType<double>(dto.Attributes["c1"]), 10);
        Assert.Equal(8.0, Assert.IsType<double>(dto.Attributes["c2"]), 10);
    }

    [Fact]
    public async Task ComputedColumn_BooleanResult_CastBack()
    {
        StubArchive(Ing("activePower"),
            Comp("isHigh", "activepower > 1000", FormulaResultType.Boolean));

        var dto = await CaptureInsertAsync(Point(new() { ["activePower"] = 1500.0 }));

        Assert.True(Assert.IsType<bool>(dto.Attributes["ishigh"]));
    }
}
