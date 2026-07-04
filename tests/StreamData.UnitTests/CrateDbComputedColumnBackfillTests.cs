using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Active-archive computed-column backfill executor (AB#4189 Phase 7, §8): page through the existing
/// rows of a live archive, evaluate the column's formula over each row's operands, and UPDATE the
/// computed cell addressed by the row key.
/// </summary>
public class CrateDbComputedColumnBackfillTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("EnergyMeter"));
    private static readonly DateTime Ts = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

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

    private static CkArchiveColumnSpec Comp(string name, string formula, ComputedColumnState state) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name,
            Formula = formula,
            ResultType = FormulaResultType.Double,
            ComputedState = state,
        };

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        params IReadOnlyDictionary<string, object?>[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private List<(string Sql, IReadOnlyList<IReadOnlyList<object?>> Args)> CaptureBulk()
    {
        var executed = new List<(string, IReadOnlyList<IReadOnlyList<object?>>)>();
        A.CallTo(() => _db.ExecuteBulkAsync("tenant-x", A<string>._,
                A<IReadOnlyList<IReadOnlyList<object?>>>._, A<CancellationToken>._))
            .Invokes(call => executed.Add((
                call.GetArgument<string>(1)!,
                call.GetArgument<IReadOnlyList<IReadOnlyList<object?>>>(2)!)))
            .Returns(Task.CompletedTask);
        return executed;
    }

    private static bool DoubleEq(double expected, object? actual) =>
        actual is double d && Math.Abs(d - expected) < 1e-9;

    private static ArchiveSnapshot RawSnapshot(params CkArchiveColumnSpec[] columns) =>
        new(Archive, Type, CkArchiveStatus.Activated, "energy", columns);

    [Fact]
    public async Task Backfill_EvaluatesFormula_AndUpdatesEachRowByKey()
    {
        var snapshot = RawSnapshot(
            Ing("ActivePower"),
            Ing("ApparentPower"),
            Comp("powerFactor", "activepower / apparentpower", ComputedColumnState.Backfilling));

        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Returns(Rows(
                new Dictionary<string, object?>
                {
                    ["timestamp"] = Ts, ["rtid"] = "rt-1", ["cktypeid"] = "EnergyMeter",
                    ["activepower"] = 8.0, ["apparentpower"] = 10.0,
                },
                new Dictionary<string, object?>
                {
                    ["timestamp"] = Ts, ["rtid"] = "rt-2", ["cktypeid"] = "EnergyMeter",
                    ["activepower"] = 5.0, ["apparentpower"] = 10.0,
                }));

        var bulk = CaptureBulk();

        await NewSut().BackfillComputedColumnAsync(snapshot, "powerFactor", CancellationToken.None);

        var (sql, args) = Assert.Single(bulk);
        // Parameterised single-target update: value = $1, then the raw key (timestamp, rtid, cktypeid).
        Assert.Contains(
            "SET \"powerfactor\" = $1 WHERE \"timestamp\" = $2 AND \"rtid\" = $3 AND \"cktypeid\" = $4", sql);
        Assert.Equal(2, args.Count);
        // Positional args: [computedValue, timestamp, rtid, cktypeid].
        Assert.Contains(args, a => DoubleEq(0.8, a[0]) && (string?)a[2] == "rt-1");
        Assert.Contains(args, a => DoubleEq(0.5, a[0]) && (string?)a[2] == "rt-2");
        Assert.All(args, a => Assert.Equal(Ts, (DateTime)a[1]!));
        // Reads must complete before writes; the backfill refreshes the table so writes become visible.
        A.CallTo(() => _db.RefreshArchiveTableAsync("tenant-x", A<string>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Backfill_NullOperand_WritesNullCell_NeverThrows()
    {
        var snapshot = RawSnapshot(
            Ing("ActivePower"),
            Ing("ApparentPower"),
            Comp("powerFactor", "activepower / apparentpower", ComputedColumnState.Backfilling));

        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Returns(Rows(new Dictionary<string, object?>
            {
                ["timestamp"] = Ts, ["rtid"] = "rt-1", ["cktypeid"] = "EnergyMeter",
                ["activepower"] = 8.0, ["apparentpower"] = null,
            }));

        var bulk = CaptureBulk();

        await NewSut().BackfillComputedColumnAsync(snapshot, "powerFactor", CancellationToken.None);

        // NULL operand ⇒ null computed cell (never throws); args[0] is the computed value.
        var (_, args) = Assert.Single(bulk);
        Assert.Null(Assert.Single(args)[0]);
    }

    [Fact]
    public async Task Backfill_TimeRangeArchive_KeysOnWindowColumns()
    {
        var snapshot = RawSnapshot(
            Ing("Energy"),
            Comp("doubled", "energy * 2", ComputedColumnState.Backfilling));
        snapshot = snapshot with { IsTimeRange = true };

        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Returns(Rows(new Dictionary<string, object?>
            {
                ["window_start"] = Ts, ["window_end"] = Ts.AddHours(1),
                ["rtid"] = "rt-1", ["cktypeid"] = "EnergyMeter", ["energy"] = 4.0,
            }));

        var bulk = CaptureBulk();

        await NewSut().BackfillComputedColumnAsync(snapshot, "doubled", CancellationToken.None);

        var (sql, args) = Assert.Single(bulk);
        // Time-range archive ⇒ keyed by (window_start, window_end, rtid, cktypeid), not timestamp.
        Assert.Contains(
            "SET \"doubled\" = $1 WHERE \"window_start\" = $2 AND \"window_end\" = $3 AND \"rtid\" = $4 AND \"cktypeid\" = $5",
            sql);
        Assert.DoesNotContain("\"timestamp\"", sql);
        var set = Assert.Single(args);
        Assert.True(DoubleEq(8.0, set[0]));                 // 4 * 2
        Assert.Equal(Ts, (DateTime)set[1]!);                // window_start
        Assert.Equal(Ts.AddHours(1), (DateTime)set[2]!);    // window_end
    }

    [Fact]
    public async Task Backfill_FormulaChange_WritesNewFormulaIntoPendingVersionedColumn()
    {
        // PendingFormula set ⇒ the backfill targets the pending versioned column with the NEW formula;
        // the active column (still serving old values) is left untouched.
        var snapshot = RawSnapshot(
            Ing("ActivePower"),
            new CkArchiveColumnSpec(string.Empty, Indexed: true, Required: false)
            {
                Name = "powerFactor",
                Formula = "activepower / 100",
                ResultType = FormulaResultType.Double,
                ComputedState = ComputedColumnState.Active,
                ComputedVersion = 0,
                PendingFormula = "activepower / 200",
            });

        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Returns(Rows(new Dictionary<string, object?>
            {
                ["timestamp"] = Ts, ["rtid"] = "rt-1", ["cktypeid"] = "EnergyMeter", ["activepower"] = 400.0,
            }));

        var bulk = CaptureBulk();

        await NewSut().BackfillComputedColumnAsync(snapshot, "powerFactor", CancellationToken.None);

        var (sql, args) = Assert.Single(bulk);
        Assert.Contains("SET \"powerfactor__v1\" = $1", sql);   // pending versioned column
        Assert.DoesNotContain("\"powerfactor\" = $1", sql);      // active column untouched
        Assert.True(DoubleEq(2.0, Assert.Single(args)[0]));      // 400 / 200
    }

    [Fact]
    public async Task Backfill_UnknownColumn_IsNoOp()
    {
        var snapshot = RawSnapshot(Ing("ActivePower"));
        var bulk = CaptureBulk();

        await NewSut().BackfillComputedColumnAsync(snapshot, "doesNotExist", CancellationToken.None);

        Assert.Empty(bulk);
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
