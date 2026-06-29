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

    private List<string> CaptureUpdates()
    {
        var executed = new List<string>();
        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Invokes(call => executed.Add(call.GetArgument<string>(1)!))
            .Returns(1);
        return executed;
    }

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

        var updates = CaptureUpdates();

        await NewSut().BackfillComputedColumnAsync(snapshot, "powerFactor", CancellationToken.None);

        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, u => u.Contains("\"powerfactor\" = 0.8") && u.Contains("\"rtid\" = 'rt-1'"));
        Assert.Contains(updates, u => u.Contains("\"powerfactor\" = 0.5") && u.Contains("\"rtid\" = 'rt-2'"));
        // Raw archive ⇒ keyed by timestamp + rtid + cktypeid.
        Assert.All(updates, u => Assert.Contains("\"timestamp\" = '2026-06-28T12:00:00.000Z'", u));
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

        var updates = CaptureUpdates();

        await NewSut().BackfillComputedColumnAsync(snapshot, "powerFactor", CancellationToken.None);

        Assert.Contains("\"powerfactor\" = NULL", Assert.Single(updates));
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

        var updates = CaptureUpdates();

        await NewSut().BackfillComputedColumnAsync(snapshot, "doubled", CancellationToken.None);

        var update = Assert.Single(updates);
        Assert.Contains("\"doubled\" = 8", update);
        Assert.Contains("\"window_start\" = '2026-06-28T12:00:00.000Z'", update);
        Assert.Contains("\"window_end\" = '2026-06-28T13:00:00.000Z'", update);
        Assert.DoesNotContain("\"timestamp\"", update);
    }

    [Fact]
    public async Task Backfill_UnknownColumn_IsNoOp()
    {
        var snapshot = RawSnapshot(Ing("ActivePower"));
        var updates = CaptureUpdates();

        await NewSut().BackfillComputedColumnAsync(snapshot, "doesNotExist", CancellationToken.None);

        Assert.Empty(updates);
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
