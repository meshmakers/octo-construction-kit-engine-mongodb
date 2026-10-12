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
using Microsoft.Extensions.Logging;
using Xunit;
using Row = Meshmakers.Octo.Runtime.Engine.UnitTests.RecomputeCrateSimulator.Row;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// The commit sequence of a rollup recompute against an in-memory CrateDB
/// (<see cref="RecomputeCrateSimulator"/>): the copy and the sweep run in row-counted slices
/// whatever the range holds, the statements and refreshes keep their order around the pointer flip,
/// rows an uncommitted copy left under the next generation number are removed first, a failure
/// behind the flip does not fail the call, and the dead rows it leaves are reclaimed from the
/// pointer table (AB#6283).
/// </summary>
public class CrateDbArchiveRecomputeExecutorTests
{
    private const string Tenant = "acmecorp";
    private const long HourMs = 3_600_000;

    private static readonly DateTime RangeStart = new(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long StartMs = new DateTimeOffset(RangeStart).ToUnixTimeMilliseconds();
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("EnergyMeter"));

    [Fact]
    public async Task LargeChunk_CopiesAndSweepsInRowBoundedSlices()
    {
        const int sliceTarget = 10;
        var harness = new Harness(sliceTarget);
        var series = Series(5);
        // 100 hourly buckets of 5 series: 500 rows staged, and 500 rows of generation 3 to supersede.
        harness.SeedLive(buckets: 100, series, generation: 3);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 100 * HourMs, string.Empty, 3));

        var result = await harness.RunAsync(buckets: 100, series);

        Assert.Equal(500, result.RowsProcessed);

        var copies = harness.Crate.Calls.Where(c => c.Kind == "copy").ToList();
        var sweeps = harness.Crate.Calls.Where(c => c.Kind == "delete:superseded").ToList();
        Assert.True(copies.Count > 1, "the copy has to run as several statements");
        Assert.True(sweeps.Count > 1, "the sweep has to run as several statements");

        AssertPartitionsTheRange(harness, copies, "count:staged", buckets: 100, sliceTarget);
        AssertPartitionsTheRange(harness, sweeps, "count:superseded", buckets: 100, sliceTarget);

        // Nothing was lost or doubled by cutting the range: exactly the staged rows, on generation 4.
        Assert.Equal(500, harness.Crate.Live.Count);
        Assert.All(harness.Crate.Live, r => Assert.Equal(4, r.Generation));
        Assert.Equal(500, harness.Crate.Live.Select(r => (r.WindowStartMs, r.RtId)).Distinct().Count());
    }

    [Fact]
    public async Task Sweep_CoversWindowsThatWereNotStaged()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        // The previous generation holds 20 buckets; the source now only has data for the first 4.
        harness.SeedLive(buckets: 20, series, generation: 1);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 20 * HourMs, string.Empty, 1));

        await harness.RunAsync(buckets: 20, series, stagedBuckets: 4);

        Assert.Equal(20, harness.Crate.Live.Count);
        Assert.All(harness.Crate.Live, r => Assert.Equal(2, r.Generation));
    }

    [Fact]
    public async Task KeepsTheCommitOrder_AndRefreshesBeforeEveryDependentStep()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.SeedLive(buckets: 12, series, generation: 3);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 12 * HourMs, string.Empty, 3));

        await harness.RunAsync(buckets: 12, series);

        Assert.Equal(
            new[]
            {
                // First call of the process for this rollup: dead rows below every pointer entry.
                "ddl:create-genmap",
                "refresh:genmap",
                "read:pointers",
                "count:below",
                // First use of the staging table by the process.
                "ddl:drop-staging",
                "ddl:create-staging",
                "aggregate",
                // The staged rows have to be on the read path before the copy reads them.
                "refresh:staging",
                // The next generation is a search read over the pointer table.
                "refresh:genmap",
                "read:next-generation",
                // The leftover guard has to see what an earlier attempt wrote.
                "refresh:live",
                "count:generation",
                "copy-slices",
                // The copied rows have to be readable before the flip makes them authoritative.
                "refresh:live",
                "flip",
                // Every reader has to select the new generation before the old one is removed.
                "refresh:genmap",
                "sweep-slices",
                // The dependents are recomputed right after; the deleted rows have to be gone.
                "refresh:live",
                "delete:contained-pointers",
                "refresh:genmap",
                // 60 staged rows are more than the slice target: dropped, not emptied.
                "ddl:drop-staging",
            },
            harness.Steps());

        // No refresh between the slices of the copy, none between the slices of the sweep.
        Assert.True(harness.Crate.Calls.Count(c => c.Kind == "copy") > 1);
        Assert.True(harness.Crate.Calls.Count(c => c.Kind == "delete:superseded") > 1);
    }

    [Fact]
    public async Task CopySliceFails_Throws_AndThePointerIsNotFlipped()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.SeedLive(buckets: 12, series, generation: 3);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 12 * HourMs, string.Empty, 3));
        var copies = 0;
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "copy" && ++copies == 2)
            {
                throw new InvalidOperationException("copy slice failed");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(buckets: 12, series));

        Assert.DoesNotContain("flip", harness.Crate.Kinds);
        Assert.DoesNotContain("delete:superseded", harness.Crate.Kinds);
        // Readers stay on generation 3, complete.
        Assert.Equal(new GenerationRange(StartMs, StartMs + 12 * HourMs, string.Empty, 3), Assert.Single(harness.Crate.Pointers));
        Assert.Equal(60, harness.Crate.Live.Count(r => r.Generation == 3));
    }

    [Fact]
    public async Task CallerCancelsDuringTheSweep_ThrowsOperationCanceled()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.SeedLive(buckets: 12, series, generation: 3);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 12 * HourMs, string.Empty, 3));
        using var cts = new CancellationTokenSource();
        harness.Token = cts.Token;
        harness.Crate.BeforeStatement = (call, token) =>
        {
            if (call.Kind == "delete:superseded")
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(buckets: 12, series));

        // The flip happened before the sweep was interrupted: the chunk is committed.
        Assert.Contains(harness.Crate.Pointers, p => p.Generation == 4);
    }

    [Fact]
    public async Task LeftoverRowsOfTheNextGeneration_AreDeletedBeforeTheCopy()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(3);
        harness.SeedLive(buckets: 6, series, generation: 3);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 6 * HourMs, string.Empty, 3));
        // A copy that was cut short before its flip left rows under generation 4 — the number the
        // next run is handed again — among them one for a series the source no longer has.
        harness.SeedLive(buckets: 6, new[] { series[0], "gone" }, generation: 4);

        await harness.RunAsync(buckets: 6, series);

        var steps = harness.Crate.Kinds.ToList();
        var guard = steps.IndexOf("delete:generation");
        Assert.True(guard >= 0, "the leftover rows have to be deleted");
        Assert.True(guard < steps.IndexOf("copy"), "… before the first copy slice");
        Assert.Equal("refresh:live", steps[steps.IndexOf("count:generation") - 1]);
        // … and they are off the read path before the copy starts.
        Assert.Equal("refresh:live", steps[steps.LastIndexOf("delete:generation") + 1]);

        Assert.Equal(18, harness.Crate.Live.Count);
        Assert.All(harness.Crate.Live, r => Assert.Equal(4, r.Generation));
        Assert.DoesNotContain(harness.Crate.Live, r => r.RtId == "gone");
    }

    [Fact]
    public async Task LeftoverGuard_IsRestrictedToTheRangeAndTheScope()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(2);
        // Rows under the next generation number outside the recomputed range, and of another series
        // inside it, are none of this run's business.
        harness.Crate.Live.Add(new Row(StartMs + 50 * HourMs, 1, series[0]));
        harness.Crate.Live.Add(new Row(StartMs, 1, series[1]));
        harness.Crate.Live.Add(new Row(StartMs, 1, series[0]));

        await harness.RunAsync(buckets: 2, new[] { series[0] }, scope: series[0]);

        Assert.Contains(new Row(StartMs + 50 * HourMs, 1, series[0]), harness.Crate.Live);
        Assert.Contains(new Row(StartMs, 1, series[1]), harness.Crate.Live);
        Assert.Equal(2, harness.Crate.Live.Count(r => r.RtId == series[0] && r.WindowStartMs < StartMs + 2 * HourMs));
    }

    // ---- committed stays committed ----

    [Fact]
    public async Task SweepFailsAfterTheFlip_ChunkIsReportedDone_AndReclaimedOnTheNextCall()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.SeedLive(buckets: 12, series, generation: 3);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 12 * HourMs, string.Empty, 3));
        var sweeps = 0;
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "delete:superseded" && ++sweeps == 2)
            {
                throw new InvalidOperationException("sweep slice failed");
            }
        };

        var result = await harness.RunAsync(buckets: 12, series);

        // Reported as done: the pointer names generation 4 and its rows are complete.
        Assert.Equal(60, result.RowsProcessed);
        Assert.Equal(new GenerationRange(StartMs, StartMs + 12 * HourMs, string.Empty, 4), Assert.Single(harness.Crate.Pointers));
        Assert.Equal(60, harness.Crate.Live.Count(r => r.Generation == 4));
        // The rest of the cleanup still ran.
        Assert.Contains("delete:contained-pointers", harness.Crate.Kinds);
        // The failure is visible: what failed, where, and that readers are fine.
        var warning = Assert.Single(harness.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("sweep of the superseded generations", warning.Message);
        Assert.Contains("generation 4", warning.Message);
        Assert.Contains($"[{StartMs},{StartMs + 12 * HourMs})", warning.Message);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        var dead = harness.Crate.Live.Count(r => r.Generation == 3);
        Assert.True(dead > 0, "the failed sweep left rows behind");

        // The next call for the rollup — another range — makes up for it before it does its own work.
        harness.Crate.BeforeStatement = null;
        var before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 20);

        var steps = harness.Crate.Kinds.Skip(before).ToList();
        Assert.True(steps.IndexOf("delete:below") >= 0, "the dead rows have to be reclaimed");
        Assert.True(steps.IndexOf("delete:below") < steps.IndexOf("aggregate"), "… before the call's own work");
        Assert.DoesNotContain(harness.Crate.Live, r => r.Generation == 3);
        Assert.Equal(60, harness.Crate.Live.Count(r => r.Generation == 4));

        // Made up for once: a third call has nothing left to reclaim.
        before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 30);
        Assert.DoesNotContain(harness.Crate.Kinds.Skip(before), k => k is "count:below" or "delete:below" or "read:pointers");
    }

    [Fact]
    public async Task PointerCleanupOrStagingCleanupFails_ChunkIsReportedDone()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(2);
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "delete:contained-pointers")
            {
                throw new InvalidOperationException("pointer cleanup failed");
            }
        };

        var result = await harness.RunAsync(buckets: 3, series);

        Assert.Equal(6, result.RowsProcessed);
        Assert.Contains(harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("removal of the contained pointer entries"));
    }

    [Fact]
    public async Task FlipFails_Throws()
    {
        var harness = new Harness(sliceTargetRows: 10);
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "flip")
            {
                throw new InvalidOperationException("flip failed");
            }
        };

        // Before the commit point nothing is swallowed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(buckets: 3, Series(2)));
    }

    // ---- reclaim from the pointer table ----

    [Fact]
    public async Task Reclaim_DeletesOnlyGenerationsBelowThePointer_InsideItsRangeAndScope()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var x = OctoObjectId.GenerateNewId().ToString();
        var y = OctoObjectId.GenerateNewId().ToString();
        // An unscoped entry at generation 5 over the first 10 hours …
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 10 * HourMs, string.Empty, 5));
        // … and an entry scoped to series x at generation 7 over hours 20 to 30.
        harness.Crate.Pointers.Add(new GenerationRange(StartMs + 20 * HourMs, StartMs + 30 * HourMs, x, 7));

        var dead = new[]
        {
            new Row(StartMs + 1 * HourMs, 3, x), // below the unscoped pointer
            new Row(StartMs + 2 * HourMs, 0, y), // forward generation inside a pointer range: masked
            new Row(StartMs + 9 * HourMs, 4, y),
            new Row(StartMs + 21 * HourMs, 6, x), // below the scoped pointer, its series
            new Row(StartMs + 22 * HourMs, 0, x),
        };
        var alive = new[]
        {
            new Row(StartMs + 1 * HourMs, 5, x), // the active generation
            new Row(StartMs + 1 * HourMs, 9, x), // a newer, overlapping recompute that has not flipped yet
            new Row(StartMs + 10 * HourMs, 3, x), // first instant behind the range (exclusive end)
            new Row(StartMs - HourMs, 3, x), // before the range
            new Row(StartMs + 15 * HourMs, 0, x), // generation 0 outside every pointer range
            new Row(StartMs + 21 * HourMs, 7, x),
            new Row(StartMs + 21 * HourMs, 6, y), // another series inside a scoped entry's range
            new Row(StartMs + 21 * HourMs, 0, y),
        };
        harness.Crate.Live.AddRange(dead);
        harness.Crate.Live.AddRange(alive);

        // A recompute of an unrelated range through a fresh executor: the first call of a process.
        await harness.RunAsync(buckets: 2, new[] { x }, firstBucket: 50);

        Assert.All(dead, row => Assert.DoesNotContain(row, harness.Crate.Live));
        Assert.All(alive, row => Assert.Contains(row, harness.Crate.Live));
        // Strictly below, never "different from".
        Assert.All(
            harness.Crate.Calls.Where(c => c.Kind is "delete:below"),
            c => Assert.Contains("\"generation\" < ", c.Text));
        // The deleted rows are off the read path before the recompute goes on.
        var steps = harness.Crate.Kinds.ToList();
        Assert.Equal("refresh:live", steps[steps.LastIndexOf("delete:below") + 1]);
    }

    [Fact]
    public async Task Reclaim_RunsInRowBoundedSlices_OncePerProcessAndRollup()
    {
        const int sliceTarget = 10;
        var harness = new Harness(sliceTarget);
        var series = Series(5);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 40 * HourMs, string.Empty, 12));
        harness.SeedLive(buckets: 40, series, generation: 12);
        harness.SeedLive(buckets: 40, series, generation: 11); // what a killed sweep left

        await harness.RunAsync(buckets: 2, series, firstBucket: 50);

        var deletes = harness.Crate.Calls.Where(c => c.Kind == "delete:below").ToList();
        Assert.True(deletes.Count > 1);
        var counts = harness.Crate.Calls.Where(c => c.Kind == "count:below")
            .ToLookup(RecomputeCrateSimulator.Bounds, c => c.Rows);
        Assert.All(deletes, d => Assert.InRange(counts[RecomputeCrateSimulator.Bounds(d)].First(), 1, 2 * sliceTarget));
        Assert.DoesNotContain(harness.Crate.Live, r => r.Generation == 11);
        Assert.Equal(200, harness.Crate.Live.Count(r => r.Generation == 12));

        // The same process does not walk the pointer table again …
        var before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 60);
        Assert.DoesNotContain("read:pointers", harness.Crate.Kinds.Skip(before));

        // … a restarted one does, once.
        harness.Executor = harness.NewExecutor();
        before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 70);
        Assert.Single(harness.Crate.Kinds.Skip(before), k => k == "read:pointers");
    }

    [Fact]
    public async Task Reclaim_StopsWhenThePointerEntryChanged()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 40 * HourMs, string.Empty, 12));
        harness.SeedLive(buckets: 40, series, generation: 11);
        // A rewind removes the pointer while the first reclaim statement runs: the range goes back
        // to generation 0, and rows below 12 there are no longer dead.
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "delete:below")
            {
                harness.Crate.Pointers.Clear();
            }
        };

        await harness.RunAsync(buckets: 2, series, firstBucket: 50);

        Assert.Single(harness.Crate.Calls, c => c.Kind == "delete:below");
        Assert.True(harness.Crate.Live.Count(r => r.Generation == 11) > 0);
        // The entry is read again before every statement.
        var steps = harness.Crate.Kinds.ToList();
        Assert.Equal("count:pointer", steps[steps.IndexOf("delete:below") - 1]);
    }

    [Fact]
    public async Task ReclaimFails_TheRecomputeStillRuns_AndTheReclaimIsRepeated()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 4 * HourMs, string.Empty, 12));
        harness.SeedLive(buckets: 4, series, generation: 11);
        var fail = true;
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (fail && call.Kind == "delete:below")
            {
                throw new InvalidOperationException("reclaim failed");
            }
        };

        var result = await harness.RunAsync(buckets: 2, series, firstBucket: 50);

        Assert.Equal(10, result.RowsProcessed);
        Assert.Contains(harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("reclaiming dead generations failed"));

        fail = false;
        await harness.RunAsync(buckets: 2, series, firstBucket: 60);
        Assert.DoesNotContain(harness.Crate.Live, r => r.Generation == 11);
    }

    [Fact]
    public async Task CallerCancelsDuringTheReclaim_ThrowsOperationCanceled()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(5);
        harness.Crate.Pointers.Add(new GenerationRange(StartMs, StartMs + 4 * HourMs, string.Empty, 12));
        harness.SeedLive(buckets: 4, series, generation: 11);
        using var cts = new CancellationTokenSource();
        harness.Token = cts.Token;
        harness.Crate.BeforeStatement = (call, token) =>
        {
            if (call.Kind == "delete:below")
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(buckets: 2, series, firstBucket: 50));
    }

    // ---- staging table ----

    [Fact]
    public async Task Staging_IsCreatedOncePerProcess_WithoutReplicas_SmallRunIssuesNoDdl_LargeRunDropsOnce()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(2);

        // First use by the process: whatever is there goes, and the table is created — one shard,
        // no replicas, whatever the archive tables are configured with.
        await harness.RunAsync(buckets: 2, series);
        var ddl = harness.Crate.Calls.Where(c => c.Kind.StartsWith("ddl:", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "ddl:create-genmap", "ddl:drop-staging", "ddl:create-staging" }, ddl.Select(c => c.Kind));
        Assert.EndsWith("CLUSTERED INTO 1 SHARDS WITH (number_of_replicas = 0);", ddl[2].Text);
        // 4 staged rows: emptied and kept.
        Assert.Equal("delete:all-staged", harness.Crate.Kinds.Last());
        Assert.True(harness.Crate.StagingExists);
        Assert.Empty(harness.Crate.Staging);

        // A second small run: no DDL at all — only the check that the kept table is empty.
        var before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 10);
        var second = harness.Crate.Kinds.Skip(before).ToList();
        Assert.DoesNotContain(second, k => k.StartsWith("ddl:", StringComparison.Ordinal));
        Assert.Equal(new[] { "refresh:staging", "count:all-staged", "aggregate" }, second.Take(3));

        // A large run (60 staged rows): still no DDL to start with, one drop at the end.
        before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 12, Series(5), firstBucket: 20);
        var third = harness.Crate.Kinds.Skip(before).ToList();
        Assert.Equal(new[] { "ddl:drop-staging" }, third.Where(k => k.StartsWith("ddl:", StringComparison.Ordinal)));
        Assert.Equal("ddl:drop-staging", third[^1]);
        Assert.DoesNotContain("delete:all-staged", third);

        // The run after it creates the table again — and does not drop first.
        before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 40);
        var fourth = harness.Crate.Kinds.Skip(before).ToList();
        Assert.Equal(new[] { "ddl:create-staging" }, fourth.Where(k => k.StartsWith("ddl:", StringComparison.Ordinal)));
        Assert.Equal("ddl:create-staging", fourth[0]);
    }

    [Fact]
    public async Task Staging_RowsOfAFailedRun_AreRemovedBeforeTheNextRunStages()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(2);
        await harness.RunAsync(buckets: 2, series);

        // A run that fails after staging leaves its rows in the kept table …
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "copy")
            {
                throw new InvalidOperationException("copy failed");
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(buckets: 3, new[] { series[0], "stale" }, firstBucket: 10));
        Assert.Equal(6, harness.Crate.Staging.Count);

        // … and the next run must not copy them: a few rows are deleted (and refreshed away) …
        harness.Crate.BeforeStatement = null;
        var before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 3, series, firstBucket: 10);
        Assert.Equal(
            new[] { "refresh:staging", "count:all-staged", "delete:all-staged", "refresh:staging", "aggregate" },
            harness.Crate.Kinds.Skip(before).Take(5));
        Assert.DoesNotContain(harness.Crate.Live, r => r.RtId == "stale");

        // … many rows are dropped with the table.
        harness.Crate.BeforeStatement = (call, _) =>
        {
            if (call.Kind == "copy")
            {
                throw new InvalidOperationException("copy failed");
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(buckets: 12, Series(5), firstBucket: 20));
        harness.Crate.BeforeStatement = null;
        before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 20);
        Assert.Equal(
            new[] { "refresh:staging", "count:all-staged", "ddl:drop-staging", "ddl:create-staging", "aggregate" },
            harness.Crate.Kinds.Skip(before).Take(5));
    }

    [Fact]
    public async Task Staging_DroppedBehindTheProcess_IsCreatedAgain()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(2);
        await harness.RunAsync(buckets: 2, series);

        // Another instance dropped the table after a large run of its own, or the archive's tables
        // were dropped and the archive came back.
        harness.Crate.StagingExists = false;

        var result = await harness.RunAsync(buckets: 2, series, firstBucket: 10);

        Assert.Equal(4, result.RowsProcessed);
        Assert.True(harness.Crate.StagingExists);
    }

    [Fact]
    public async Task Staging_AnotherProcess_StartsWithAFreshTable()
    {
        var harness = new Harness(sliceTargetRows: 10);
        var series = Series(2);
        await harness.RunAsync(buckets: 2, series);
        // A crashed process left rows in the table it had created.
        harness.Crate.Staging.Add(new Row(StartMs + 10 * HourMs, 0, "stale"));

        harness.Executor = harness.NewExecutor();
        var before = harness.Crate.Calls.Count;
        await harness.RunAsync(buckets: 2, series, firstBucket: 10);

        var ddl = harness.Crate.Kinds.Skip(before).Where(k => k.StartsWith("ddl:", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "ddl:create-genmap", "ddl:drop-staging", "ddl:create-staging" }, ddl);
        Assert.DoesNotContain(harness.Crate.Live, r => r.RtId == "stale");
    }

    // ---- helpers ----

    private static string[] Series(int count) =>
        Enumerable.Range(0, count).Select(_ => OctoObjectId.GenerateNewId().ToString()).ToArray();

    /// <summary>
    /// The statements of one kind partition <c>[start, start + buckets h)</c> without gap or
    /// overlap, and none was executed on a slice that was counted above twice the target.
    /// </summary>
    private static void AssertPartitionsTheRange(
        Harness harness, IReadOnlyList<RecomputeCrateSimulator.Call> statements, string countKind, int buckets,
        int sliceTarget)
    {
        var counts = harness.Crate.Calls.Where(c => c.Kind == countKind)
            .ToLookup(RecomputeCrateSimulator.Bounds, c => c.Rows);

        var next = StartMs;
        var rowsCovered = 0L;
        foreach (var statement in statements)
        {
            var (from, to) = RecomputeCrateSimulator.Bounds(statement);
            Assert.True(from >= next, "slices must not overlap");
            Assert.True(to > from);
            // The gap between two executed slices is made of slices that were counted empty.
            var counted = Assert.Single(counts[(from, to)].Distinct());
            Assert.InRange(counted, 1, 2 * sliceTarget);
            rowsCovered += counted;
            next = to;
        }

        Assert.True(next <= StartMs + buckets * HourMs);
        Assert.Equal(buckets * 5L, rowsCovered);
    }

    private sealed class Harness
    {
        private readonly int _sliceTargetRows;
        private readonly IArchiveRuntimeStore _archiveStore = A.Fake<IArchiveRuntimeStore>();
        private readonly OctoObjectId _rollupRtId = OctoObjectId.GenerateNewId();
        private readonly ArchiveSnapshot _source;
        private readonly RollupArchiveSnapshot _rollup;

        public Harness(int sliceTargetRows)
        {
            _sliceTargetRows = sliceTargetRows;
            var specs = new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Sum, null) };
            _source = new ArchiveSnapshot(OctoObjectId.GenerateNewId(), MeterType, CkArchiveStatus.Activated, "raw",
                new List<CkArchiveColumnSpec> { new("Voltage", Indexed: true, Required: false) });
            _rollup = new RollupArchiveSnapshot(
                _rollupRtId, MeterType, CkArchiveStatus.Activated, "hourly",
                new[] { new RollupSourceReference(_source.RtId) },
                TimeSpan.FromHours(1), TimeSpan.Zero, null, specs, null);
            var rollupArchive = new ArchiveSnapshot(_rollupRtId, MeterType, CkArchiveStatus.Activated, "hourly",
                RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = TimeSpan.FromHours(1),
            };
            A.CallTo(() => _archiveStore.GetAsync(_rollupRtId)).Returns(rollupArchive);

            Crate = new RecomputeCrateSimulator(Tenant, _rollupRtId.ToString());
            Executor = NewExecutor();
        }

        public RecomputeCrateSimulator Crate { get; }

        public RecordingLogger Logger { get; } = new();

        public CrateDbArchiveRecomputeExecutor Executor { get; set; }

        /// <summary>The token every run is started with.</summary>
        public CancellationToken Token { get; set; } = TestContext.Current.CancellationToken;

        /// <summary>A second executor on the same tables — what a restarted process would be.</summary>
        public CrateDbArchiveRecomputeExecutor NewExecutor() => new(
            Tenant, Crate, Crate, _archiveStore, numberOfShards: 3, numberOfReplicas: -1,
            Logger, rollupArchiveStore: null, sliceTargetRows: _sliceTargetRows);

        public void SeedLive(int buckets, IReadOnlyList<string> series, long generation, int firstBucket = 0)
        {
            for (var bucket = firstBucket; bucket < firstBucket + buckets; bucket++)
            {
                foreach (var rtId in series)
                {
                    Crate.Live.Add(new Row(StartMs + bucket * HourMs, generation, rtId));
                }
            }
        }

        /// <summary>
        /// Recomputes <paramref name="buckets"/> hourly buckets from <paramref name="firstBucket"/>;
        /// the source yields one row per series for the first <paramref name="stagedBuckets"/> of
        /// them (all when null).
        /// </summary>
        public Task<RecomputeExecutionResult> RunAsync(
            int buckets, IReadOnlyList<string> series, string? scope = null, int? stagedBuckets = null,
            int firstBucket = 0)
        {
            var bucket = 0;
            Crate.StageForAggregate = _ =>
            {
                var current = bucket++;
                return current < (stagedBuckets ?? buckets)
                    ? series.Select(rtId => new Row(StartMs + (firstBucket + current) * HourMs, 0, rtId))
                    : Array.Empty<Row>();
            };

            return Executor.ExecuteAsync(
                _source, _rollup, RangeStart.AddHours(firstBucket), RangeStart.AddHours(firstBucket + buckets),
                scope is null ? null : OctoObjectId.Parse(scope), Token);
        }

        /// <summary>
        /// The recorded calls as steps: runs of the same call collapsed, and the count / statement
        /// pairs of the copy and the sweep collapsed into one step each.
        /// </summary>
        public IReadOnlyList<string> Steps(int skip = 0)
        {
            var steps = new List<string>();
            foreach (var kind in Crate.Kinds.Skip(skip))
            {
                var step = kind switch
                {
                    "count:staged" or "copy" => "copy-slices",
                    "count:superseded" or "delete:superseded" => "sweep-slices",
                    _ => kind,
                };
                if (steps.Count == 0 || steps[^1] != step)
                {
                    steps.Add(step);
                }
            }

            return steps;
        }
    }
}
