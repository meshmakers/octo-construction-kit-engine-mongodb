using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#5967 slice 2 (AB#6968): the dump of a rollup copies its generation map first and applies the repeat rule;
/// a load takes the data first and the map last and verifies the row counts. Against the simulated CrateDB; a
/// <c>Fault</c> hook flips the pointer map on demand.
/// </summary>
public class RollupArchiveDumperTests
{
    private static readonly OctoObjectId Rollup = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId Restored = OctoObjectId.GenerateNewId();

    private static readonly ArchiveCopyTarget RollupTarget =
        new(Rollup, "tenant1", SimulatedCrate.SourceTable, "window_start", SimulatedCrate.SourceTable + "__genmap");

    private static readonly ArchiveCopyTarget PlainTarget = new(Rollup, "tenant1", SimulatedCrate.SourceTable, "timestamp");

    private static readonly ArchiveCopyTarget RestoreTarget =
        new(Restored, "tenant1", SimulatedCrate.TargetTable, "timestamp", SimulatedCrate.TargetTable + "__genmap");

    private static readonly ArchiveCopyStagingLocation Location = new(
        "s3://CANARYKEYID1234:CANARY/SECRET+value==@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1",
        new Dictionary<string, string> { ["protocol"] = "http" });

    private static readonly ArchiveCopyOptions Small = new() { TargetSliceRows = 4 };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SimulatedCrate _crate = new();

    private static DateTime At(int day, int hour) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);

    private RollupArchiveDumper Dumper(TimeProvider? clock = null)
        => new(NullLogger.Instance, "tenant1", _crate,
            new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate), clock ?? new TickingClock());

    private SimulatedCrate WithRollup(int generations = 3)
    {
        for (var h = 0; h < 10; h++) _crate.SourceRows.Add(At(1, h));
        for (var g = 1; g <= generations; g++)
        {
            _crate.SourceMap.Add(new SimulatedCrate.MapRow(g * 1000, g * 1000 + 500, "", g));
        }

        return _crate;
    }

    private static bool IsMapCopyTo(string sql) => sql.Contains("__genmap", StringComparison.Ordinal) && sql.Contains(" TO DIRECTORY ", StringComparison.Ordinal);

    private static bool IsDataCopyTo(string sql) => !sql.Contains("__genmap", StringComparison.Ordinal) && sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains(" TO DIRECTORY ", StringComparison.Ordinal);

    // ---- the plan --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_OfARollup_EndsWithTheGenerationMapSlice_AndDescribesBothTables()
    {
        WithRollup(generations: 16);

        var plan = await new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate).PlanAsync(RollupTarget, Small, Ct);

        Assert.Equal(ArchiveCopyTable.GenerationMap, plan.Slices[^1].Table);
        Assert.Equal(16, plan.Slices[^1].RowCount);
        Assert.Equal(1, plan.Slices.Count(s => s.Table == ArchiveCopyTable.GenerationMap));
        Assert.Equal([ArchiveCopyTable.Data, ArchiveCopyTable.GenerationMap], plan.Tables.Select(t => t.Table));
        Assert.Equal(["range_start", "range_end", "rtid_scope", "generation"], plan.Tables[1].Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Plan_OfARollupWithAnEmptyOrMissingMap_HasNoMapSlice()
    {
        _crate.SourceRows.Add(At(1, 0));

        var empty = await new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate).PlanAsync(RollupTarget, Small, Ct);
        Assert.DoesNotContain(empty.Slices, s => s.Table == ArchiveCopyTable.GenerationMap);
        Assert.Equal(2, empty.Tables.Count);

        _crate.MapColumns = [];
        var missing = await new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate).PlanAsync(RollupTarget, Small, Ct);
        Assert.DoesNotContain(missing.Slices, s => s.Table == ArchiveCopyTable.GenerationMap);
        Assert.Single(missing.Tables);
    }

    // ---- the dump --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dump_OfARollup_CopiesTheMapBeforeTheData_AndReadsItBeforeAndAfter()
    {
        WithRollup();

        var result = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Attempts);
        var order = _crate.Executed.Where(s => IsMapCopyTo(s) || IsDataCopyTo(s)).Select(IsMapCopyTo).ToList();
        Assert.True(order[0], "the generation map is copied first");
        Assert.All(order.Skip(1), isMap => Assert.False(isMap));
        Assert.Equal(2, _crate.MapReads); // before the copy and after the last data slice
        Assert.Single(_crate.MapStore);
        Assert.Equal(_crate.SourceMap, _crate.MapStore.Values.Single());
    }

    [Fact]
    public async Task Dump_StatesTheTimeOfTheMapReadAndOfTheLastDataSlice_AndTheMapIsNeverReadAfterTheData()
    {
        WithRollup();
        var clock = new TickingClock();

        var result = await Dumper(clock).DumpAsync(RollupTarget, Location, Small, Ct);

        Assert.NotNull(result.MapReadAtUtc);
        Assert.NotNull(result.LastDataCopiedAtUtc);
        Assert.True(result.MapReadAtUtc < result.LastDataCopiedAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.MapReadAtUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, result.LastDataCopiedAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task Dump_TimesAreMonotonic_EvenWhenTheClockStepsBackwards()
    {
        WithRollup();
        var clock = new TickingClock { StepBackwardsAtCall = 2 };

        var result = await Dumper(clock).DumpAsync(RollupTarget, Location, Small, Ct);

        Assert.True(clock.SteppedBackwards, "the test clock did step backwards");
        Assert.True(result.MapReadAtUtc <= result.LastDataCopiedAtUtc);
    }

    [Fact]
    public async Task Dump_RepeatsTheRollup_WhenThePointerMapMovedBetweenTheReads_AndReportsTheAttempts()
    {
        WithRollup();
        var flipped = false;
        _crate.Fault = statement =>
        {
            // A recompute flips a pointer while the first data slice is copied.
            if (!flipped && IsDataCopyTo(statement.Reveal()))
            {
                flipped = true;
                _crate.SourceMap[0] = _crate.SourceMap[0] with { Generation = 4 };
            }
        };

        var result = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(4, _crate.MapReads); // before + after, twice
        // the files that stand carry the map of the final attempt, and it is the map the table has now
        Assert.Equal(_crate.SourceMap, _crate.MapStore.Values.Single());
        Assert.Equal(4, _crate.MapStore.Values.Single()[0].Generation);
        // the second attempt copied the data again: two passes over the same slices
        Assert.Equal(2, _crate.Executed.Count(IsMapCopyTo));
    }

    [Fact]
    public async Task Dump_FailsAfterTheBoundedAttempts_AndNamesTheRollup_WhenTheMapMovesEveryTime()
    {
        WithRollup();
        var generation = 10L;
        _crate.Fault = statement =>
        {
            if (IsDataCopyTo(statement.Reveal()))
            {
                _crate.SourceMap[0] = _crate.SourceMap[0] with { Generation = ++generation };
            }
        };
        var options = new ArchiveCopyOptions { TargetSliceRows = 4, MaxMapAttempts = 3 };

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() => Dumper().DumpAsync(RollupTarget, Location, options, Ct));

        Assert.Equal(ArchiveCopyRefusalReason.GenerationMapKeptMoving, ex.Reason);
        Assert.Contains(Rollup.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("3 attempts", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, _crate.Executed.Count(IsMapCopyTo)); // bounded: not a fourth
    }

    [Fact]
    public async Task Dump_PlanCarriesTheRowsTheCopyReported_SoTheLoadVerifiesWhatIsInTheFiles()
    {
        WithRollup();
        var added = false;
        _crate.Fault = statement =>
        {
            // The tenant stays online: a row lands in the slice between the count and the copy.
            if (!added && IsDataCopyTo(statement.Reveal()))
            {
                added = true;
                _crate.SourceRows.Add(At(1, 5).AddMinutes(30));
            }
        };

        var dump = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);

        Assert.True(dump.IsSuccess);
        Assert.Equal(11, dump.Plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).Sum(s => s.RowCount));
        Assert.Equal(3, dump.Plan.Slices.Single(s => s.Table == ArchiveCopyTable.GenerationMap).RowCount);
        // a load of that plan finds exactly the rows in the files
        var runner = new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate);
        var results = new List<ArchiveCopySliceResult>();
        await foreach (var r in runner.CopyFromAsync(RestoreTarget, dump.Plan, Location, Small, Ct))
        {
            results.Add(r);
        }

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
    }

    [Fact]
    public async Task Dump_TellsTheObserverTheTwoPhasesOfEveryAttempt_InOrder()
    {
        WithRollup();
        var events = new List<string>();
        var flipped = false;
        _crate.Fault = statement =>
        {
            if (IsDataCopyTo(statement.Reveal()))
            {
                events.Add("data");
                if (!flipped)
                {
                    flipped = true;
                    _crate.SourceMap[0] = _crate.SourceMap[0] with { Generation = 9 }; // a recompute flips during attempt 1
                }
            }
        };
        var options = Small with { PhaseObserver = (phase, attempt, _) => { events.Add($"{phase}#{attempt}"); return Task.CompletedTask; } };

        var result = await Dumper().DumpAsync(RollupTarget, Location, options, Ct);

        Assert.Equal(2, result.Attempts);
        var phases = events.Where(e => e != "data").ToList();
        Assert.Equal(["MapRead#1", "DataCopied#1", "MapRead#2", "DataCopied#2"], phases);
        Assert.True(events.IndexOf("MapRead#1") < events.IndexOf("data"));
        Assert.True(events.IndexOf("data") < events.IndexOf("DataCopied#1"));
    }

    [Fact]
    public async Task Dump_WithoutTheRepeatRule_CopiesOnce_DoesNotCompareTheMaps_AndReturnsNormally()
    {
        WithRollup();
        _crate.Fault = statement =>
        {
            if (IsDataCopyTo(statement.Reveal()))
            {
                _crate.SourceMap[0] = _crate.SourceMap[0] with { Generation = 9 };
            }
        };

        var result = await Dumper().DumpAsync(RollupTarget, Location, Small with { RepeatOnMovedMap = false, MaxMapAttempts = 1 }, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, _crate.MapReads); // only the read before the copy
        Assert.NotEqual(9, _crate.MapStore.Values.Single()[0].Generation); // the files carry the map as it was copied
    }

    [Fact]
    public async Task Dump_OfAnArchiveWithoutAMap_NeverReadsOrCopiesOne()
    {
        WithRollup();

        var result = await Dumper().DumpAsync(PlainTarget, Location, Small, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Attempts);
        Assert.Null(result.MapReadAtUtc);
        Assert.NotNull(result.LastDataCopiedAtUtc);
        Assert.DoesNotContain(_crate.Executed, s => s.Contains("__genmap", StringComparison.Ordinal));
        Assert.Equal(0, _crate.MapReads);
        Assert.Empty(_crate.MapStore);
    }

    [Fact]
    public async Task Dump_WithAFailedSlice_ReturnsTheFailure_WithoutRepeating()
    {
        WithRollup();
        _crate.Fault = statement =>
        {
            if (IsDataCopyTo(statement.Reveal()))
            {
                throw new StreamDataCopyStatementException(statement.Redacted, "XX000", "disk full");
            }
        };

        var result = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, _crate.MapReads); // no second read after a failed slice
    }

    [Fact]
    public void SameMap_ComparesRangesScopesAndGenerations()
    {
        var a = new[] { new RollupArchiveDumper.MapEntry(1, 2, "", 3) };

        Assert.True(RollupArchiveDumper.SameMap(a, [new RollupArchiveDumper.MapEntry(1, 2, "", 3)]));
        Assert.False(RollupArchiveDumper.SameMap(a, [new RollupArchiveDumper.MapEntry(1, 2, "", 4)]));
        Assert.False(RollupArchiveDumper.SameMap(a, [new RollupArchiveDumper.MapEntry(1, 2, "x", 3)]));
        Assert.False(RollupArchiveDumper.SameMap(a, []));
    }

    // ---- the load --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_TakesTheDataBeforeTheMap_AndVerifiesTheRowCounts()
    {
        WithRollup(generations: 16);
        var runner = new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate);
        var dump = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);
        _crate.Executed.Clear();

        var outcome = new LoadOutcome();
        var results = new List<ArchiveCopySliceResult>();
        await foreach (var r in runner.CopyFromAsync(RestoreTarget, dump.Plan, Location, Small, outcome, Ct))
        {
            results.Add(r);
        }

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(ArchiveCopyTable.GenerationMap, results[^1].Slice.Table); // the map last
        var loads = _crate.Executed.Where(s => s.StartsWith("COPY", StringComparison.Ordinal) && s.Contains(" FROM ", StringComparison.Ordinal)).ToList();
        Assert.Contains("__genmap", loads[^1], StringComparison.Ordinal);
        Assert.All(loads.Take(loads.Count - 1), l => Assert.DoesNotContain("__genmap", l, StringComparison.Ordinal));
        Assert.Equal(10, outcome.DataRows);
        Assert.Equal(16, outcome.MapRows);
        Assert.Equal(_crate.SourceMap.OrderBy(m => m.Start), _crate.TargetMap.OrderBy(m => m.Start)); // identical pointers
    }

    [Fact]
    public async Task Load_IntoAnArchiveWithoutAMap_RefusesAPlanWithAMap()
    {
        WithRollup();
        var runner = new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate);
        var dump = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);
        var noMap = new ArchiveCopyTarget(Restored, "tenant1", SimulatedCrate.TargetTable, "timestamp");

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(async () =>
        {
            await foreach (var _ in runner.CopyFromAsync(noMap, dump.Plan, Location, Small, Ct))
            {
            }
        });

        Assert.Equal(ArchiveCopyRefusalReason.ColumnMissing, ex.Reason);
        Assert.Empty(_crate.TargetRows);
    }

    [Fact]
    public async Task Load_RefusesWhenTheNewMapTableLacksAColumn()
    {
        WithRollup();
        var runner = new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate);
        var dump = await Dumper().DumpAsync(RollupTarget, Location, Small, Ct);
        _crate.MapColumns = ["range_start", "range_end", "rtid_scope"]; // no generation column

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(async () =>
        {
            await foreach (var _ in runner.CopyFromAsync(RestoreTarget, dump.Plan, Location, Small, Ct))
            {
            }
        });

        Assert.Contains("generation map table", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'generation'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Returns a time that advances one second per call; can step back once to prove monotonicity.</summary>
    private sealed class TickingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        private int _calls;

        public int StepBackwardsAtCall { get; init; }

        public bool SteppedBackwards { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            _calls++;
            if (_calls == StepBackwardsAtCall)
            {
                SteppedBackwards = true;
                return _now.AddSeconds(-30);
            }

            _now = _now.AddSeconds(1);
            return _now;
        }
    }
}
