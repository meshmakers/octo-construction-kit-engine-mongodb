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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Row = Meshmakers.Octo.Runtime.Engine.UnitTests.RecomputeCrateSimulator.Row;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// The commit sequence of a rollup recompute against an in-memory CrateDB
/// (<see cref="RecomputeCrateSimulator"/>): the copy and the sweep run in row-counted slices
/// whatever the range holds, the statements and refreshes keep their order around the pointer flip,
/// and rows an uncommitted copy left under the next generation number are removed first (AB#6283).
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
                "ddl:drop-staging",
                "ddl:create-staging",
                "aggregate",
                // The staged rows have to be on the read path before the copy reads them.
                "refresh:staging",
                "ddl:create-genmap",
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

        public CrateDbArchiveRecomputeExecutor Executor { get; set; }

        /// <summary>The token every run is started with.</summary>
        public CancellationToken Token { get; set; } = TestContext.Current.CancellationToken;

        /// <summary>A second executor on the same tables — what a restarted process would be.</summary>
        public CrateDbArchiveRecomputeExecutor NewExecutor() => new(
            Tenant, Crate, Crate, _archiveStore, numberOfShards: 3, numberOfReplicas: -1,
            NullLogger.Instance, rollupArchiveStore: null, sliceTargetRows: _sliceTargetRows);

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
