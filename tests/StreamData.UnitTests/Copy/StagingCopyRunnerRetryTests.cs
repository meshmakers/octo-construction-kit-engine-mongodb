using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#7274: a slice that CrateDB refuses for a transient reason (memory circuit breaker, rejected execution, timeout,
/// or a summary that is short with <c>error_count = 0</c>) is loaded again, at most three attempts in total; every repeat
/// overwrites duplicates and halves the batch size. The simulated CrateDB parses the statements the runner sends.
/// </summary>
public class StagingCopyRunnerRetryTests
{
    private const string CanarySecret = "CANARY/SECRET+value==";
    private const string BreakerText =
        "CircuitBreakingException[[parent] Data too large, data for [upsert request] would be larger than limit]";

    private static readonly OctoObjectId Source = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId Restored = OctoObjectId.GenerateNewId();

    private static readonly ArchiveCopyTarget SourceTarget = new(Source, "tenant1", SimulatedCrate.SourceTable, "timestamp");
    private static readonly ArchiveCopyTarget RestoreTarget = new(Restored, "tenant1", SimulatedCrate.TargetTable, "timestamp");

    private static readonly ArchiveCopyTarget RollupSource =
        new(Source, "tenant1", SimulatedCrate.SourceTable, "timestamp", SimulatedCrate.SourceTable + "__genmap");

    private static readonly ArchiveCopyTarget RollupRestore =
        new(Restored, "tenant1", SimulatedCrate.TargetTable, "timestamp", SimulatedCrate.TargetTable + "__genmap");

    private static readonly ArchiveCopyStagingLocation Location = new(
        $"s3://CANARYKEYID1234:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1",
        new Dictionary<string, string> { ["protocol"] = "http", ["sas_token"] = "CANARYSAS" });

    private static readonly ArchiveCopyOptions Small = new() { TargetSliceRows = 4 };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SimulatedCrate _crate = new();
    private readonly InstantTimeProvider _time = new();
    private readonly CapturingLogger _log = new();

    private StagingCopyRunner Runner() => new(_log, "tenant1", _crate, _time);

    private static DateTime At(int day, int hour) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);

    private async Task<ArchiveCopyPlan> Dumped(ArchiveCopyTarget target, bool withMap = false)
    {
        for (var h = 0; h < 10; h++) _crate.SourceRows.Add(At(1, h));
        for (var h = 0; h < 4; h++) _crate.SourceRows.Add(At(2, h * 6));
        if (withMap)
        {
            _crate.SourceMap.Add(new SimulatedCrate.MapRow(1000, 1500, "", 1));
            _crate.SourceMap.Add(new SimulatedCrate.MapRow(2000, 2500, "", 2));
        }

        var runner = Runner();
        var plan = await runner.PlanAsync(target, Small, Ct);
        await Collect(runner.CopyToAsync(target, plan, Location, Small, Ct));
        _crate.Executed.Clear();
        _crate.Shown.Clear();
        return plan;
    }

    private static async Task<List<ArchiveCopySliceResult>> Collect(IAsyncEnumerable<ArchiveCopySliceResult> results)
    {
        var list = new List<ArchiveCopySliceResult>();
        await foreach (var r in results)
        {
            list.Add(r);
        }

        return list;
    }

    private List<string> DataLoads() => _crate.Executed
        .Where(s => s.StartsWith("COPY", StringComparison.Ordinal) && s.Contains(" FROM ", StringComparison.Ordinal)
                    && !s.Contains("__genmap", StringComparison.Ordinal)).ToList();

    private static StreamDataCopyStatementException Breaker(CopyStatement statement)
        => new(statement.Redacted, "XX000", BreakerText);

    /// <summary>Fails the first <paramref name="times"/> data loads with <paramref name="failure"/>.</summary>
    private void FailDataLoads(int times, Func<CopyStatement, Exception> failure)
    {
        var seen = 0;
        _crate.Fault = statement =>
        {
            if (statement.IsCopy && statement.Reveal().Contains(" FROM ", StringComparison.Ordinal)
                && !statement.Reveal().Contains("__genmap", StringComparison.Ordinal) && ++seen <= times)
            {
                throw failure(statement);
            }
        };
    }

    // ---- the first attempt is today's statement ---------------------------------------------------------------

    [Fact]
    public async Task FirstAttempt_SendsExactlyTodaysStatement_AndNothingIsRetriedWhenNothingFails()
    {
        var plan = await Dumped(SourceTarget);

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var expected = plan.Slices.Select(s => CopyStatement.CopyFrom(RestoreTarget.QualifiedTable,
            Location.WithSubPath(s.GetStagingPath(Source)), overwriteDuplicates: false).Reveal()).ToList();
        Assert.Equal(expected, DataLoads());
        Assert.DoesNotContain(DataLoads(), l => l.Contains("bulk_size", StringComparison.Ordinal));
        Assert.Empty(_time.Delays);
        Assert.Equal(0, _crate.HeapReads);
    }

    // ---- retried, then complete -----------------------------------------------------------------------------

    [Fact]
    public async Task CircuitBreaker_ThenSuccess_RepeatsWithOverwriteAndHalvedBulkSize_AfterTheBackoff()
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(1, Breaker);

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(plan.Slices.Count, results.Count);
        var loads = DataLoads();
        Assert.Equal(plan.Slices.Count + 1, loads.Count);
        Assert.Contains("overwrite_duplicates = false", loads[0]);
        Assert.DoesNotContain("bulk_size", loads[0]);
        Assert.Contains("overwrite_duplicates = true", loads[1]);
        Assert.Contains("bulk_size = 5000", loads[1]);
        Assert.Equal(loads[1], CopyStatement.CopyFrom(RestoreTarget.QualifiedTable,
            Location.WithSubPath(plan.Slices[0].GetStagingPath(Source)), true, 5000).Reveal());
        Assert.Equal([TimeSpan.FromSeconds(30)], _time.Delays);
        Assert.Equal(1, _crate.HeapReads);
        Assert.Equal(_crate.SourceRows.Order(), _crate.TargetRows.Order());
        Assert.Contains(_log.Lines, l => l.Contains("attempt 1 of 3 failed", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("loaded on attempt 2 of 3", StringComparison.Ordinal));
        // the settings were put back once, after the last slice
        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
    }

    [Fact]
    public async Task ShortSummaryWithoutErrors_IsRepeated_AndTheRowsTheFirstAttemptWroteAreNotDuplicated()
    {
        var plan = await Dumped(SourceTarget);
        _crate.LoadScript.Enqueue((2, 0, null)); // two rows loaded, no error counted, slice has more

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Contains("overwrite_duplicates = true", DataLoads()[1]);
        Assert.Equal(_crate.SourceRows.Order(), _crate.TargetRows.Order());
        Assert.Equal(_crate.TargetRows.Count, _crate.TargetRows.Distinct().Count());
        // the per-node summary rows of the short attempt are in the log
        Assert.Contains(_log.Lines, l => l.Contains("success_count 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransientRowErrors_AreRepeated()
    {
        var plan = await Dumped(SourceTarget);
        _crate.LoadScript.Enqueue((2, 3, "{\"" + BreakerText + "\": {\"count\": 3}}"));

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(plan.Slices.Count + 1, DataLoads().Count);
        Assert.Equal(_crate.SourceRows.Order(), _crate.TargetRows.Order());
    }

    [Fact]
    public async Task Timeout_IsRepeated()
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(1, s => new StreamDataStatementTimeoutException(CrateStatementClass.Maintenance, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(121), "COPY t FROM '***'", null));

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(plan.Slices.Count + 1, DataLoads().Count);
    }

    [Fact]
    public async Task ALocationThatSetsItsOwnBulkSize_IsRepeatedWithIt_TheStatementDoesNotSetASecondOne()
    {
        // Review of AB#7274: the halved bulk_size of a repeat collided with a location option and threw an ArgumentException
        // out of the load instead of repeating the slice.
        var plan = await Dumped(SourceTarget);
        var location = new ArchiveCopyStagingLocation(
            $"s3://CANARYKEYID1234:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1",
            new Dictionary<string, string> { ["protocol"] = "http", ["bulk_size"] = "1000" });
        FailDataLoads(1, Breaker);

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var loads = DataLoads();
        Assert.Equal(plan.Slices.Count + 1, loads.Count);
        Assert.Contains("overwrite_duplicates = true", loads[1]);
        Assert.All(loads, l => Assert.Single(System.Text.RegularExpressions.Regex.Matches(l, "bulk_size = ")));
        Assert.All(loads, l => Assert.Contains("bulk_size = 1000", l));
    }

    [Fact]
    public async Task SecondAttemptFailsToo_ThirdAttemptUsesAQuarterOfTheBatch_AfterTheLongerBackoff()
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(2, Breaker);

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var loads = DataLoads();
        Assert.Contains("bulk_size = 5000", loads[1]);
        Assert.Contains("bulk_size = 2500", loads[2]);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)], _time.Delays);
    }

    // ---- the merge after the load fits a small heap ---------------------------------------------------------

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1_000, 3, 1)]
    [InlineData(30_000_000, 3, 1)]          // exactly 10 million rows per shard: one segment, as before
    [InlineData(30_000_001, 3, 2)]
    [InlineData(119_462_768, 3, 4)]         // the sbeg raw archive: 39.8 million rows per shard, merged to 4 segments of about 10 million
    [InlineData(150_000_000, 1, 15)]
    public void TheMergeAimsAtOneSegmentPer10MillionRowsOfAShard(long rows, int shards, int expected)
        => Assert.Equal(expected, StagingCopyRunner.SegmentsPerShard(rows, shards, StagingCopyRunner.DefaultMaxDocsPerMergedSegment));

    [Fact]
    public async Task AnArchiveBiggerThanTheSegmentLimit_IsMergedToSeveralSegmentsPerShard_ASmallOneToOne()
    {
        var plan = await Dumped(SourceTarget);

        await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));
        Assert.Contains("max_num_segments = 1", Assert.Single(_crate.Executed, s => s.StartsWith("OPTIMIZE", StringComparison.Ordinal)));

        _crate.Executed.Clear();
        _crate.TargetRows.Clear();
        // 14 rows over 3 shards, at most 2 rows in a merged segment: 5 rows per shard, 3 segments
        var runner = new StagingCopyRunner(_log, "tenant1", _crate, _time, maxDocsPerMergedSegment: 2);
        await Collect(runner.CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));
        Assert.Contains("max_num_segments = 3", Assert.Single(_crate.Executed, s => s.StartsWith("OPTIMIZE", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailedMerge_AndANodeThatNeedsAMomentToComeBack_DoNotFailAnArchiveWhoseRowsAreComplete()
    {
        var plan = await Dumped(SourceTarget);
        var refreshesAfterMerge = 0;
        var merged = false;
        _crate.Fault = statement =>
        {
            var sql = statement.Reveal();
            if (sql.StartsWith("OPTIMIZE", StringComparison.Ordinal))
            {
                merged = true;
                throw new StreamDataCopyStatementException(statement.Redacted, null, "Exception while reading from stream");
            }

            if (merged && sql.StartsWith("REFRESH", StringComparison.Ordinal) && ++refreshesAfterMerge <= 2)
            {
                throw new StreamDataCopyStatementException(statement.Redacted, null, "Exception while reading from stream");
            }
        };

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(_crate.SourceRows.Order(), _crate.TargetRows.Order());
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)], _time.Delays);
        Assert.Contains(_log.Lines, l => l.Contains("The segment merge of archive", StringComparison.Ordinal));
    }

    // ---- not retried ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("column 'value' unknown")]
    [InlineData("Cannot read the file: no such key")]
    [InlineData("Access denied")]
    public async Task NonTransientStatementError_FailsAtOnce_AsToday(string reason)
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(10, s => new StreamDataCopyStatementException(s.Redacted, "XX000", reason));

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        var failed = Assert.Single(results);
        Assert.False(failed.IsSuccess);
        Assert.DoesNotContain("attempts", failed.Message);
        Assert.Single(DataLoads());
        Assert.Empty(_time.Delays);
        Assert.Equal(0, _crate.HeapReads);
    }

    [Fact]
    public async Task RowErrorsThatAreNotAllTransient_FailAtOnce()
    {
        var plan = await Dumped(SourceTarget);
        _crate.LoadScript.Enqueue((2, 3, "{\"parse error\": {\"count\": 3}}"));

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        var failed = Assert.Single(results);
        Assert.False(failed.IsSuccess);
        Assert.Equal(3, failed.ErrorRows);
        Assert.Single(DataLoads());
    }

    [Fact]
    public async Task MoreRowsThanExpected_IsNotRepeated()
    {
        var plan = await Dumped(SourceTarget);
        _crate.LoadScript.Enqueue((99, 0, null));

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.False(Assert.Single(results).IsSuccess);
        Assert.Single(DataLoads());
    }

    // ---- all attempts failed ----------------------------------------------------------------------------------

    [Fact]
    public async Task ThreeFailedAttempts_EndTheArchive_RestoreTheSettings_AndDoNotMerge()
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(100, Breaker);

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        var failed = Assert.Single(results);
        Assert.False(failed.IsSuccess);
        Assert.Contains("after 3 attempts", failed.Message);
        Assert.Equal(3, DataLoads().Count);
        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("OPTIMIZE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARetriedSliceWhoseRangeHoldsTheWrongNumberOfRows_CountsAsFailed()
    {
        var plan = await Dumped(SourceTarget);
        _crate.TargetRows.Add(At(1, 0).AddMinutes(1)); // a stray row inside the first slice that is not in the dump
        FailDataLoads(1, Breaker);

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        var failed = Assert.Single(results);
        Assert.False(failed.IsSuccess);
        Assert.Contains("rows are in the table for the slice", failed.Message);
        Assert.Contains("after 3 attempts", failed.Message);
    }

    // ---- the generation map -------------------------------------------------------------------------------------

    [Fact]
    public async Task GenerationMapSlice_IsRetriedTheSameWay()
    {
        var plan = await Dumped(RollupSource, withMap: true);
        var seen = 0;
        _crate.Fault = statement =>
        {
            if (statement.IsCopy && statement.Reveal().Contains("__genmap", StringComparison.Ordinal)
                && statement.Reveal().Contains(" FROM ", StringComparison.Ordinal) && ++seen == 1)
            {
                throw Breaker(statement);
            }
        };

        var results = await Collect(Runner().CopyFromAsync(RollupRestore, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(ArchiveCopyTable.GenerationMap, results[^1].Slice.Table);
        var mapLoads = _crate.Executed.Where(s => s.StartsWith("COPY", StringComparison.Ordinal)
            && s.Contains("__genmap", StringComparison.Ordinal) && s.Contains(" FROM ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mapLoads.Count);
        Assert.Contains("overwrite_duplicates = true", mapLoads[1]);
        Assert.Contains("bulk_size = 5000", mapLoads[1]);
        Assert.Equal(_crate.SourceMap.Count, _crate.TargetMap.Count);
    }

    // ---- cancellation and credential ------------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_DuringTheBackoff_StopsTheCopy_AndPutsTheSettingsBack()
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(100, Breaker);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _time.Fire = false;
        _time.OnTimer = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, cts.Token)));

        Assert.Single(DataLoads());
        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
    }

    [Fact]
    public async Task NothingOfTheRetry_LogLinesAndResults_CarriesTheCredential()
    {
        var plan = await Dumped(SourceTarget);
        FailDataLoads(100, s => new StreamDataCopyStatementException(
            s.Redacted, "XX000", $"{BreakerText} while reading s3://CANARYKEYID1234:{CanarySecret}@minio.octo-infra:9000/x"));

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        var texts = _log.Lines.Concat(results.Select(r => r.Message ?? string.Empty)).Concat(_crate.Shown).ToList();
        Assert.NotEmpty(_log.Lines);
        Assert.All(texts, t =>
        {
            Assert.DoesNotContain("CANARY", t);
            Assert.DoesNotContain("SECRET+value", t);
        });
    }

    // ---- fakes -----------------------------------------------------------------------------------------------------

    /// <summary>A clock whose timers fire the moment they are armed (or never), and which records the delays asked for.</summary>
    private sealed class InstantTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];
        public bool Fire { get; set; } = true;
        public Action<TimeSpan>? OnTimer { get; set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new InstantTimer(this, callback, state);
            timer.Change(dueTime, period); // Task.Delay arms the timer through CreateTimer
            return timer;
        }

        private sealed class InstantTimer(InstantTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return true;
                }

                owner.Delays.Add(dueTime);
                owner.OnTimer?.Invoke(dueTime);
                if (owner.Fire)
                {
                    callback(state);
                }

                return true;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
