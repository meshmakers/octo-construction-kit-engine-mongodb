using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
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
        // The single-point insert goes through the batch path (AB#7598).
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._, A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Invokes(call => captured = call.GetArgument<IEnumerable<DataPointDto>>(3)!.Single())
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));

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
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._, A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Invokes(call => columns = call.GetArgument<IReadOnlyList<string>>(2))
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));

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

    // ---- NULL operands (AB#8060) ------------------------------------------------------------------
    // An on-change source sends a row with only one of two attributes. The missing operand is bound as
    // NaN (the formula engine's NULL), so the formula decides — it is no longer NULL as a whole.

    private const string DischargeFormula =
        "if(chargingpower<0,-chargingpower,if(coalesce(dischargingpower,0)>0,dischargingpower,0))";

    [Fact]
    public async Task ComputedColumn_MissingOperandInTheBranchNotTaken_StillComputes()
    {
        StubArchive(Ing("chargingPower"), Ing("dischargingPower"),
            Comp("dischargePower", "if(chargingpower<0,-chargingpower,dischargingpower)", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["chargingPower"] = -4.0 }));

        Assert.Equal(4.0, Assert.IsType<double>(dto.Attributes["dischargepower"]), 10);
    }

    [Theory]
    [InlineData(-4.0, null, 4.0)]  // only chargingpower, battery discharging
    [InlineData(2.0, null, 0.0)]   // only chargingpower, battery charging
    [InlineData(-4.0, 0.0, 4.0)]   // both
    [InlineData(0.0, 6.0, 6.0)]    // both, discharge reported positively
    public async Task ComputedColumn_EnergyIqDischargeFormula_WithCoalesce(double charging, double? discharging,
        double expected)
    {
        StubArchive(Ing("chargingPower"), Ing("dischargingPower"),
            Comp("dischargePower", DischargeFormula, FormulaResultType.Double));
        var attributes = new Dictionary<string, object?> { ["chargingPower"] = charging };
        if (discharging is { } d)
        {
            attributes["dischargingPower"] = d;
        }

        var dto = await CaptureInsertAsync(Point(attributes));

        Assert.Equal(expected, Assert.IsType<double>(dto.Attributes["dischargepower"]), 10);
    }

    [Fact]
    public async Task ComputedColumn_OnlyTheOtherOperand_CoalescedFormulaComputes()
    {
        // Only dischargingpower: a formula that coalesces both operands still gives a value.
        StubArchive(Ing("chargingPower"), Ing("dischargingPower"),
            Comp("dischargePower",
                "if(coalesce(chargingpower,0)<0,-chargingpower,if(coalesce(dischargingpower,0)>0,dischargingpower,0))",
                FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["dischargingPower"] = 3.0 }));

        Assert.Equal(3.0, Assert.IsType<double>(dto.Attributes["dischargepower"]), 10);
    }

    [Fact]
    public async Task ComputedColumn_UnhandledNullInArithmetic_StoresNull()
    {
        // The documented rule: an unhandled NULL operand in arithmetic makes the result NULL.
        StubArchive(Ing("chargingPower"), Ing("dischargingPower"),
            Comp("net", "chargingpower + dischargingpower", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["chargingPower"] = -4.0, ["dischargingPower"] = null }));

        Assert.Null(dto.Attributes["net"]);
    }

    [Fact]
    public async Task ComputedColumn_NonNumericOperand_IsBoundAsNull()
    {
        StubArchive(Ing("label"), Ing("value"),
            Comp("safe", "coalesce(label, value)", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["label"] = "not-a-number", ["value"] = 7.0 }));

        Assert.Equal(7.0, Assert.IsType<double>(dto.Attributes["safe"]), 10);
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

    // ---- Partial deliveries (AB#8622) -------------------------------------------------------------
    // A column the point does not name keeps its stored value. A computed column is not sent by anyone; it is
    // left out of the row when the point names none of the columns its formula reads, so a delivery of another
    // column does not overwrite it with a value computed from nothing.

    [Fact]
    public async Task ComputedColumn_PointNamesNoneOfItsOperands_IsLeftOutOfTheRow()
    {
        StubArchive(Ing("activePower"), Ing("voltage"), Comp("derived", "activepower * 2", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["voltage"] = 230.0 }));

        Assert.False(dto.Attributes.ContainsKey("derived"));
        Assert.Equal(230.0, dto.Attributes["voltage"]);
    }

    [Fact]
    public async Task ComputedColumn_OperandSentAsExplicitNull_IsStillEvaluated()
    {
        StubArchive(Ing("activePower"), Ing("voltage"), Comp("derived", "activepower * 2", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["activePower"] = null }));

        Assert.True(dto.Attributes.ContainsKey("derived"));
        Assert.Null(dto.Attributes["derived"]);
    }

    [Fact]
    public async Task ComputedColumn_OverAnotherComputedColumn_FollowsItsSource()
    {
        StubArchive(Ing("activePower"), Ing("voltage"),
            Comp("doubled", "activepower * 2", FormulaResultType.Double),
            Comp("quadrupled", "doubled * 2", FormulaResultType.Double));

        var skipped = await CaptureInsertAsync(Point(new() { ["voltage"] = 230.0 }));
        var evaluated = await CaptureInsertAsync(Point(new() { ["activePower"] = 1.5 }));

        Assert.False(skipped.Attributes.ContainsKey("doubled"));
        Assert.False(skipped.Attributes.ContainsKey("quadrupled"));
        Assert.Equal(6.0, Assert.IsType<double>(evaluated.Attributes["quadrupled"]), 10);
    }

    [Fact]
    public async Task ComputedColumn_WithoutColumnReferences_IsAlwaysEvaluated()
    {
        StubArchive(Ing("activePower"), Comp("constant", "42", FormulaResultType.Double));

        var dto = await CaptureInsertAsync(Point(new() { ["somethingElse"] = 1.0 }));

        Assert.Equal(42.0, Assert.IsType<double>(dto.Attributes["constant"]), 10);
    }

    // ---- Merged row (AB#8622, decided by Reimar 2026-11-09) ------------------------------------------
    // A point that names only some inputs of a formula gets the others from the stored row of its key.

    private const string NetFormula = "consumption - production";

    private static readonly DateTime At = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly OctoObjectId Meter = OctoObjectId.GenerateNewId();

    private static StreamDataPoint MeterPoint(Dictionary<string, object?> attrs) => new()
    {
        RtId = Meter,
        CkTypeId = SomeType,
        Timestamp = At,
        Attributes = attrs,
    };

    private static string MeterKey => new StoredRowKey(Meter.ToString(), SomeType.ToString(), At, null).Value;

    private void StubNetArchive() => StubArchive(Ing("consumption"), Ing("production"),
        Comp("net", NetFormula, FormulaResultType.Double));

    private List<(IReadOnlyList<StoredRowKey> Keys, IReadOnlyList<string> Columns)> StubStoredRows(
        Dictionary<string, IReadOnlyDictionary<string, object?>> rows)
    {
        var calls = new List<(IReadOnlyList<StoredRowKey>, IReadOnlyList<string>)>();
        A.CallTo(() => _db.ReadStoredRowsAsync("tenant-x", A<string>._, A<bool>._, A<IReadOnlyList<StoredRowKey>>._,
                A<IReadOnlyList<string>>._))
            .Invokes(call => calls.Add((call.GetArgument<IReadOnlyList<StoredRowKey>>(3)!.ToList(),
                call.GetArgument<IReadOnlyList<string>>(4)!.ToList())))
            .Returns(Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>>(rows));
        return calls;
    }

    private async Task<List<DataPointDto>> CaptureBatchAsync(params StreamDataPoint[] points)
    {
        List<DataPointDto>? captured = null;
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._, A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Invokes(call => captured = call.GetArgument<IEnumerable<DataPointDto>>(3)!.ToList())
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));

        await NewSut().InsertAsync(Archive, points);
        return captured!;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, object?>> StoredMeter(double consumption, double production) =>
        new(StringComparer.Ordinal)
        {
            [MeterKey] = new Dictionary<string, object?>
            {
                ["timestamp"] = At, ["rtid"] = Meter.ToString(), ["cktypeid"] = SomeType.ToString(),
                ["consumption"] = consumption, ["production"] = production,
            }
        };

    [Fact]
    public async Task MergedRow_PartialInput_TakesTheOtherInputFromTheStoredRow()
    {
        StubNetArchive();
        var calls = StubStoredRows(StoredMeter(10.0, 4.0));

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["consumption"] = 12.0 }));

        Assert.Equal(8.0, Assert.IsType<double>(dto.Attributes["net"]), 10);
        // The stored value is a formula input only: the row still names only what was sent plus the result.
        Assert.False(dto.Attributes.ContainsKey("production"));
        var call = Assert.Single(calls);
        Assert.Equal(MeterKey, Assert.Single(call.Keys).Value);
        Assert.Equal(["production"], call.Columns);
    }

    [Fact]
    public async Task MergedRow_ExplicitNullInput_TheFormulaSeesNull_AndNothingIsRead()
    {
        StubNetArchive();
        var calls = StubStoredRows(StoredMeter(10.0, 4.0));

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["consumption"] = 12.0, ["production"] = null }));

        Assert.Null(dto.Attributes["net"]);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task MergedRow_AllInputsSent_NothingIsRead()
    {
        StubNetArchive();
        var calls = StubStoredRows(StoredMeter(10.0, 4.0));

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["consumption"] = 12.0, ["production"] = 5.0 }));

        Assert.Equal(7.0, Assert.IsType<double>(dto.Attributes["net"]), 10);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task MergedRow_NoInputSent_ComputedColumnUntouched_AndNothingIsRead()
    {
        StubArchive(Ing("consumption"), Ing("production"), Ing("voltage"),
            Comp("net", NetFormula, FormulaResultType.Double));
        var calls = StubStoredRows(StoredMeter(10.0, 4.0));

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["voltage"] = 230.0 }));

        Assert.False(dto.Attributes.ContainsKey("net"));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task MergedRow_NewRow_UnsentInputIsNull()
    {
        StubNetArchive();
        StubStoredRows(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal));

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["consumption"] = 12.0 }));

        Assert.Null(dto.Attributes["net"]);
    }

    [Fact]
    public async Task MergedRow_StoredNullInput_IsNull()
    {
        StubNetArchive();
        var stored = StoredMeter(10.0, 4.0);
        ((Dictionary<string, object?>)stored[MeterKey])["production"] = null;
        StubStoredRows(stored);

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["consumption"] = 12.0 }));

        Assert.Null(dto.Attributes["net"]);
    }

    [Fact]
    public async Task MergedRow_SameKeyTwiceInOneBatch_TheLaterPointSeesTheEarlierOne()
    {
        StubNetArchive();
        var calls = StubStoredRows(StoredMeter(10.0, 4.0));

        var dtos = await CaptureBatchAsync(
            MeterPoint(new() { ["production"] = 5.0 }),
            MeterPoint(new() { ["consumption"] = 12.0 }));

        Assert.Equal(5.0, Assert.IsType<double>(dtos[0].Attributes["net"]), 10);  // 10 stored - 5
        Assert.Equal(7.0, Assert.IsType<double>(dtos[1].Attributes["net"]), 10);  // 12 - 5 of the first point
        Assert.Single(Assert.Single(calls).Keys);
    }

    [Fact]
    public async Task MergedRow_FullPointBeforeAPartialOfTheSameKey_ThePartialSeesTheFullPoint()
    {
        StubNetArchive();
        StubStoredRows(StoredMeter(10.0, 4.0));

        var dtos = await CaptureBatchAsync(
            MeterPoint(new() { ["consumption"] = 20.0, ["production"] = 1.0 }),
            MeterPoint(new() { ["consumption"] = 12.0 }));

        Assert.Equal(19.0, Assert.IsType<double>(dtos[0].Attributes["net"]), 10);
        Assert.Equal(11.0, Assert.IsType<double>(dtos[1].Attributes["net"]), 10);
    }

    [Fact]
    public async Task MergedRow_MixedBatch_OneReadForAllPartialRows()
    {
        StubNetArchive();
        var other = OctoObjectId.GenerateNewId();
        var otherKey = new StoredRowKey(other.ToString(), SomeType.ToString(), At, null).Value;
        var stored = StoredMeter(10.0, 4.0);
        stored[otherKey] = new Dictionary<string, object?> { ["consumption"] = 100.0, ["production"] = 30.0 };
        var calls = StubStoredRows(stored);

        var dtos = await CaptureBatchAsync(
            MeterPoint(new() { ["consumption"] = 12.0 }),
            new StreamDataPoint { RtId = other, CkTypeId = SomeType, Timestamp = At, Attributes = new Dictionary<string, object?> { ["production"] = 40.0 } },
            new StreamDataPoint { RtId = OctoObjectId.GenerateNewId(), CkTypeId = SomeType, Timestamp = At,
                Attributes = new Dictionary<string, object?> { ["consumption"] = 3.0, ["production"] = 1.0 } });

        Assert.Equal(8.0, Assert.IsType<double>(dtos[0].Attributes["net"]), 10);
        Assert.Equal(60.0, Assert.IsType<double>(dtos[1].Attributes["net"]), 10);
        Assert.Equal(2.0, Assert.IsType<double>(dtos[2].Attributes["net"]), 10);
        var call = Assert.Single(calls);
        Assert.Equal(2, call.Keys.Count);
        Assert.Equal(["consumption", "production"], call.Columns);
    }

    [Fact]
    public async Task MergedRow_ComputedInputLeftOut_IsReadFromTheStoredRow()
    {
        // total = doubled + voltage; a point with only voltage leaves doubled out, so total reads the stored doubled.
        StubArchive(Ing("activePower"), Ing("voltage"),
            Comp("doubled", "activepower * 2", FormulaResultType.Double),
            Comp("total", "doubled + voltage", FormulaResultType.Double));
        var calls = StubStoredRows(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            [MeterKey] = new Dictionary<string, object?> { ["doubled"] = 6.0, ["activepower"] = 3.0, ["voltage"] = 1.0 }
        });

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["voltage"] = 230.0 }));

        Assert.False(dto.Attributes.ContainsKey("doubled"));
        Assert.Equal(236.0, Assert.IsType<double>(dto.Attributes["total"]), 10);
        Assert.Equal(["doubled"], Assert.Single(calls).Columns);
    }

    [Fact]
    public async Task MergedRow_ComputedInputRecomputed_UsesTheNewValue()
    {
        StubArchive(Ing("activePower"), Ing("voltage"),
            Comp("doubled", "activepower * 2", FormulaResultType.Double),
            Comp("total", "doubled + voltage", FormulaResultType.Double));
        var calls = StubStoredRows(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            [MeterKey] = new Dictionary<string, object?> { ["doubled"] = 6.0, ["voltage"] = 1.0 }
        });

        var dto = await CaptureInsertAsync(MeterPoint(new() { ["activePower"] = 5.0 }));

        Assert.Equal(10.0, Assert.IsType<double>(dto.Attributes["doubled"]), 10);
        Assert.Equal(11.0, Assert.IsType<double>(dto.Attributes["total"]), 10);  // new doubled + stored voltage
        Assert.Equal(["voltage"], Assert.Single(calls).Columns);
    }
}
