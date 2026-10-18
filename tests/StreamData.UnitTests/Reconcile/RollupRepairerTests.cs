using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Reconcile;

/// <summary>
/// AB#6968: what the repair plans, in which order, what a dry run and a failing step leave alone, and when the marker is cleared -
/// against a scripted database that records the statements it is sent. The recompute itself is covered against a real CrateDB.
/// </summary>
public class RollupRepairerTests
{
    private const string Tenant = "acmecorp";
    private const long HourMs = 3_600_000;
    private static readonly long T0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Sensor"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OctoObjectId _rollup = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _source = OctoObjectId.GenerateNewId();
    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly IArchiveRuntimeStore _archives = A.Fake<IArchiveRuntimeStore>();
    private readonly IRollupArchiveRuntimeStore _rollups = A.Fake<IRollupArchiveRuntimeStore>();
    private readonly IStreamDataRepository _repository = A.Fake<IStreamDataRepository>();
    private readonly RecomputeProcessState _state = new();
    private readonly List<string> _executed = [];

    private bool _genMapExists = true;
    private bool _markerPending;
    private long _orphans;
    private DateTime? _maxWindowEnd;
    private long _sourceRows;
    private Func<string, Exception?> _fail = _ => null;

    private static DateTime At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public RollupRepairerTests()
    {
        A.CallTo(() => _db.GetCountAsync(A<string>._, A<string>._, A<CrateStatementClass>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CrateStatementClass _, CancellationToken _) => Task.FromResult(Count(sql)));
        A.CallTo(() => _db.RefreshArchiveTableAsync(A<string>._, A<string>._, A<CrateStatementClass>._, A<CancellationToken>._)).Returns(Task.CompletedTask);
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) => Rows(sql));
        A.CallTo(() => _db.ExecuteNonQueryAsync(A<string>._, A<string>._, A<CrateStatementClass>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CrateStatementClass _, CancellationToken _) =>
            {
                _executed.Add(sql);
                if (_fail(sql) is { } ex)
                {
                    throw ex;
                }

                if (sql.StartsWith("DELETE FROM \"acmecorp\".\"__restore_reconcile\"", StringComparison.Ordinal))
                {
                    _markerPending = false;
                }

                return Task.FromResult(1);
            });
        A.CallTo(() => _mgmt.ExecuteDdlAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) =>
            {
                _executed.Add(sql);
                if (sql.Contains("__genmap", StringComparison.Ordinal))
                {
                    _genMapExists = true;
                }

                return Task.CompletedTask;
            });
        A.CallTo(() => _archives.GetAsync(_source)).Returns(new ArchiveSnapshot(_source, Type, CkArchiveStatus.Activated, "raw", []));
    }

    private long Count(string sql)
    {
        if (sql.Contains("__restore_reconcile", StringComparison.Ordinal))
        {
            return _markerPending ? 1 : 0;
        }

        if (sql.Contains("information_schema.tables", StringComparison.Ordinal))
        {
            return sql.Contains("__genmap", StringComparison.Ordinal) ? (_genMapExists ? 1 : 0) : 1;
        }

        if (sql.Contains("NOT (", StringComparison.Ordinal) || sql.EndsWith("WHERE \"generation\" = 9;", StringComparison.Ordinal))
        {
            return _orphans;
        }

        if (sql.Contains(_source.ToString(), StringComparison.Ordinal))
        {
            return _sourceRows;
        }

        throw new NotSupportedException($"Unexpected count: {sql}");
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(string sql)
    {
        await Task.CompletedTask;
        if (sql.Contains("GROUP BY", StringComparison.Ordinal))
        {
            if (_orphans > 0)
            {
                yield return new Dictionary<string, object?> { ["g"] = 9L, ["c"] = _orphans, ["lo"] = At(T0), ["hi"] = At(T0 + 5 * HourMs) };
            }
        }
        else if (sql.Contains("max_end", StringComparison.Ordinal))
        {
            yield return new Dictionary<string, object?> { ["max_end"] = _maxWindowEnd };
        }
    }

    private RollupArchiveSnapshot Snapshot(DateTime? watermark = null) => new(
        _rollup, Type, CkArchiveStatus.Activated, "hourly", [new RollupSourceReference(_source)], TimeSpan.FromHours(1), TimeSpan.Zero,
        watermark, new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) }, null);

    private RollupRepairer Repairer(RollupArchiveSnapshot rollup)
    {
        A.CallTo(() => _rollups.EnumerateAsync()).ReturnsLazily(() => Enumerate(rollup));
        A.CallTo(() => _rollups.GetAsync(_rollup)).Returns(rollup);
        var validator = new RollupConsistencyValidator(Tenant, _db, _archives, _rollups, NullLogger.Instance, TimeProvider.System);
        var marker = new RestoreReconcileMarker(Tenant, _db, _mgmt, _state, TimeProvider.System, NullLogger.Instance);
        return new RollupRepairer(Tenant, _db, _mgmt, _archives, _rollups, _repository, validator, marker,
            () => throw new NotSupportedException("no recompute in this test"), NullLogger.Instance);
    }

    private static async IAsyncEnumerable<RollupArchiveSnapshot> Enumerate(RollupArchiveSnapshot snapshot)
    {
        await Task.CompletedTask;
        yield return snapshot;
    }

    [Fact]
    public async Task ConsistentRollup_HasNoSteps_AndTheMarkerIsCleared()
    {
        _markerPending = true;

        var report = await Repairer(Snapshot()).RepairAsync(null, dryRun: false, Ct);

        Assert.True(report.IsOk);
        Assert.Empty(report.Actions);
        Assert.False(_markerPending);
        Assert.Contains(_executed, s => s.StartsWith("DELETE FROM \"acmecorp\".\"__restore_reconcile\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingMapAndOrphans_AreRepairedInOrder_TheMapFirst_TheOrphansBeforeAnythingElse()
    {
        _genMapExists = false;
        _orphans = 7;

        // the second validation (after the repair) sees the map and no orphans any more
        var steps = new List<string>();
        _fail = sql =>
        {
            steps.Add(sql);
            if (sql.StartsWith("DELETE FROM", StringComparison.Ordinal) && sql.Contains("\"generation\" = 9", StringComparison.Ordinal))
            {
                _orphans = 0;
            }

            return null;
        };

        var report = await Repairer(Snapshot()).RepairAsync(null, dryRun: false, Ct);

        Assert.Equal([RollupRepairKind.CreateGenerationMap, RollupRepairKind.DeleteOrphans], report.Actions.Select(a => a.Kind));
        Assert.All(report.Actions, a => Assert.Equal(RollupRepairStatus.Done, a.Status));
        var create = _executed.FindIndex(s => s.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal) && s.Contains("__genmap", StringComparison.Ordinal));
        var delete = _executed.FindIndex(s => s.StartsWith("DELETE FROM", StringComparison.Ordinal) && s.Contains("\"generation\" = 9", StringComparison.Ordinal));
        Assert.InRange(create, 0, delete - 1);
        Assert.Equal(RollupConsistencyCheck.MissingGenerationMap, report.Before.Rollups.Single().Findings[0].Check);
        Assert.True(report.After!.IsOk);
        Assert.True(report.IsOk);
    }

    [Fact]
    public async Task DryRun_ListsThePlannedSteps_ExecutesNothing_AndKeepsTheMarker()
    {
        _genMapExists = false;
        _orphans = 7;
        _markerPending = true;

        var report = await Repairer(Snapshot()).RepairAsync(null, dryRun: true, Ct);

        Assert.True(report.DryRun);
        Assert.Null(report.After);
        Assert.Equal(2, report.Actions.Count);
        Assert.All(report.Actions, a => Assert.Equal(RollupRepairStatus.Planned, a.Status));
        Assert.Empty(_executed);
        Assert.True(_markerPending);
        Assert.True(report.IsOk); // a dry run changed nothing and failed nowhere
    }

    [Fact]
    public async Task AFailingStep_IsNamed_EndsTheRepairOfItsRollup_AndKeepsTheMarker()
    {
        _genMapExists = false;
        _orphans = 7;
        _markerPending = true;
        _fail = sql => sql.StartsWith("DELETE FROM", StringComparison.Ordinal) && sql.Contains("\"generation\" = 9", StringComparison.Ordinal)
            ? new InvalidOperationException("CrateDB is unavailable")
            : null;

        var report = await Repairer(Snapshot()).RepairAsync(null, dryRun: false, Ct);

        Assert.False(report.IsOk);
        Assert.Equal(RollupRepairStatus.Done, report.Actions[0].Status); // the map was created
        Assert.Equal(RollupRepairStatus.Failed, report.Actions[1].Status);
        Assert.Equal("CrateDB is unavailable", report.Actions[1].Error);
        Assert.Contains(_rollup.ToString(), report.FailedStep, StringComparison.Ordinal);
        Assert.Contains("generation 9", report.FailedStep, StringComparison.Ordinal);
        Assert.Contains("CrateDB is unavailable", report.FailedStep, StringComparison.Ordinal);
        Assert.True(_markerPending);
        Assert.DoesNotContain(_executed, s => s.StartsWith("DELETE FROM \"acmecorp\".\"__restore_reconcile\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WatermarkAheadOfTheRows_IsRewoundToTheEndOfTheData_AndTheGenerationsBehindItAreCleared()
    {
        _maxWindowEnd = At(T0 + 10 * HourMs);
        _sourceRows = 50;

        var report = await Repairer(Snapshot(watermark: At(T0 + 16 * HourMs))).RepairAsync(null, dryRun: false, Ct);

        var action = Assert.Single(report.Actions);
        Assert.Equal(RollupRepairKind.RewindWatermark, action.Kind);
        Assert.Equal(RollupRepairStatus.Done, action.Status);
        A.CallTo(() => _rollups.AdvanceWatermarkAsync(_rollup, At(T0 + 10 * HourMs), true)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.ClearRecomputeGenerationsAsync(_rollup, At(T0 + 10 * HourMs), A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }
}
