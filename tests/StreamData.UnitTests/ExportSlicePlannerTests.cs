using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// The planner behind the archive export scan: the slices it lets through must cover the range
/// exactly, whatever the data looks like, and none may hold far more rows than the target.
/// </summary>
public class ExportSlicePlannerTests
{
    private const long Day = 86_400_000;

    private sealed record Outcome(List<(long From, long To, long Rows)> Accepted, int Counts);

    /// <summary>
    /// Runs the planner the way the export does: count the proposal, let the planner decide.
    /// <paramref name="sortedInstants"/> are the instants of the rows, ascending.
    /// </summary>
    private static Outcome Run(long from, long to, long[] sortedInstants, long target)
    {
        var planner = new ExportSlicePlanner(from, to, sortedInstants.Length, target);
        var accepted = new List<(long, long, long)>();
        var counts = 0;
        while (planner.TryNext(out var a, out var b))
        {
            long rows = UpperBound(sortedInstants, b) - UpperBound(sortedInstants, a);
            counts++;
            if (planner.Accept(rows))
            {
                accepted.Add((a, b, rows));
            }

            Assert.True(counts < 200_000, "the planner does not terminate");
        }

        return new Outcome(accepted, counts);
    }

    /// <summary>Number of instants strictly below <paramref name="value"/>.</summary>
    private static long UpperBound(long[] sorted, long value)
    {
        var index = Array.BinarySearch(sorted, value);
        if (index < 0)
        {
            return ~index;
        }

        while (index > 0 && sorted[index - 1] == value)
        {
            index--;
        }

        return index;
    }

    private static void AssertPartition(Outcome outcome, long from, long to, long rows, long target)
    {
        var slices = outcome.Accepted;
        Assert.NotEmpty(slices);
        Assert.Equal(from, slices[0].From);
        Assert.Equal(to, slices[^1].To);
        for (var i = 0; i < slices.Count; i++)
        {
            Assert.True(slices[i].To > slices[i].From);
            if (i > 0)
            {
                Assert.Equal(slices[i - 1].To, slices[i].From); // no gap, no overlap
            }

            // Nothing is read that holds more than the tolerance, except a single instant.
            Assert.True(slices[i].Rows <= target * ExportSlicePlanner.Tolerance || slices[i].To - slices[i].From == 1,
                $"slice {i} holds {slices[i].Rows} rows over {slices[i].To - slices[i].From} ms");
        }

        Assert.Equal(rows, slices.Sum(s => s.Rows));
    }

    [Fact]
    public void FewRows_OneSliceOverTheWholeRange()
    {
        var outcome = Run(10, 31, [10, 20, 30], target: 1000);

        Assert.Single(outcome.Accepted);
        Assert.Equal((10L, 31L, 3L), outcome.Accepted[0]);
        Assert.Equal(1, outcome.Counts);
    }

    [Fact]
    public void EmptyRange_NoSlice()
    {
        var planner = new ExportSlicePlanner(100, 100, rowCount: 0, targetRows: 10);
        Assert.False(planner.TryNext(out _, out _));
    }

    [Fact]
    public void EvenData_SlicesHoldAboutTheTarget_OneCountEach()
    {
        // One row a second for ten days, target 10,000 rows.
        var instants = Enumerable.Range(0, 864_000).Select(i => i * 1000L).ToArray();
        var outcome = Run(0, 864_000_000, instants, target: 10_000);

        AssertPartition(outcome, 0, 864_000_000, instants.Length, 10_000);
        Assert.InRange(outcome.Accepted.Count, 80, 95);
        Assert.All(outcome.Accepted.Take(outcome.Accepted.Count - 1), s => Assert.InRange(s.Rows, 9_000, 11_000));
        Assert.Equal(outcome.Accepted.Count, outcome.Counts); // no proposal was refused
    }

    [Fact]
    public void SparseThenDense_NoSliceSwallowsTheDensePart()
    {
        // The shape of an archive that was back-filled: a year with one row a day, then thirty
        // days with a row every second. A width extrapolated from the sparse year reaches far
        // into the dense month; the count before the read stops that.
        var sparse = Enumerable.Range(0, 365).Select(d => d * Day);
        var dense = Enumerable.Range(0, 30 * 86_400).Select(s => 365 * Day + s * 1000L);
        var instants = sparse.Concat(dense).ToArray();
        var to = 395 * Day;

        var outcome = Run(0, to, instants, target: 50_000);

        AssertPartition(outcome, 0, to, instants.Length, 50_000);
        // 2.6 million rows at 50,000 a slice: about 52 slices in the dense month, few before it.
        Assert.InRange(outcome.Accepted.Count(s => s.Rows > 0), 40, 110);
        Assert.True(outcome.Counts < 3 * outcome.Accepted.Count, $"{outcome.Counts} counts for {outcome.Accepted.Count} slices");
    }

    [Fact]
    public void Gap_IsCrossedInFewCountsAndNoRead()
    {
        // Dense data, five empty years, dense data again.
        var first = Enumerable.Range(0, 100_000).Select(i => (long)i);
        var second = Enumerable.Range(0, 100_000).Select(i => 5 * 365 * Day + i);
        var instants = first.Concat(second).ToArray();
        var to = 5 * 365 * Day + 100_000;

        var outcome = Run(0, to, instants, target: 10_000);

        AssertPartition(outcome, 0, to, instants.Length, 10_000);
        // Crossing the gap at the width of the dense part would take 15 million slices. Widening
        // fourfold over the gap and narrowing fourfold at its far edge takes a number of counts
        // that grows with the logarithm of that ratio, and no read at all.
        Assert.True(outcome.Counts < 250, $"{outcome.Counts} counts");
    }

    [Fact]
    public void ManyRowsOnOneInstant_AreReadAsOneSlice()
    {
        // 50,000 rows share each of three instants (every series of a tenant has the same window
        // start). Time cannot split an instant, so such a slice is read although it is over the target.
        var instants = Enumerable.Repeat(0L, 50_000).Concat(Enumerable.Repeat(1L, 50_000)).Concat(Enumerable.Repeat(2L, 50_000)).ToArray();
        var outcome = Run(0, 3, instants, target: 1_000);

        AssertPartition(outcome, 0, 3, instants.Length, 1_000);
        Assert.Equal(3, outcome.Accepted.Count);
        Assert.All(outcome.Accepted, s => Assert.Equal(50_000, s.Rows));
    }

    [Fact]
    public void AllRowsAtTheVeryStart_StillConverges()
    {
        // A slice whose rows all sit at its first instant: every proposal from that start is too
        // large until it is one millisecond wide, which is then read.
        var instants = Enumerable.Repeat(0L, 1_000_000).ToArray();
        var outcome = Run(0, 1_000_000_000_000, instants, target: 1_000);

        AssertPartition(outcome, 0, 1_000_000_000_000, instants.Length, 1_000);
        Assert.True(outcome.Counts < 200, $"{outcome.Counts} counts");
    }

    [Fact]
    public void RefusedSlice_IsProposedAgainFromTheSameStart_Narrower()
    {
        var planner = new ExportSlicePlanner(0, 1_000_000, rowCount: 1_000, targetRows: 1_000);
        Assert.True(planner.TryNext(out var a, out var b));
        Assert.Equal((0L, 1_000_000L), (a, b));

        Assert.False(planner.Accept(countedRows: 3_000)); // three times the target
        Assert.True(planner.TryNext(out a, out b));
        Assert.Equal(0, a);
        Assert.Equal(333_333, b); // even spread: a third

        Assert.False(planner.Accept(countedRows: 100_000)); // a hundred times the target
        Assert.True(planner.TryNext(out a, out b));
        Assert.Equal(0, a);
        Assert.Equal(333_333 / ExportSlicePlanner.MaxGrowth, b); // narrowed by the limit, not to a hundredth

        Assert.True(planner.Accept(countedRows: 1_500)); // inside the tolerance
        Assert.True(planner.TryNext(out a, out _));
        Assert.Equal(333_333 / ExportSlicePlanner.MaxGrowth, a);
    }

    [Fact]
    public void EmptySlice_GrowsTheNextByTheLimit()
    {
        var planner = new ExportSlicePlanner(0, 1_000_000_000, rowCount: 10_000_000, targetRows: 1_000);
        Assert.True(planner.TryNext(out var a, out var b));
        var width = b - a;

        Assert.True(planner.Accept(countedRows: 0));
        Assert.True(planner.TryNext(out a, out b));
        Assert.Equal(width * ExportSlicePlanner.MaxGrowth, b - a);
    }

    [Fact]
    public void HugeRange_DoesNotOverflow()
    {
        // The whole range a timestamp can hold, empty.
        var planner = new ExportSlicePlanner(long.MinValue, long.MaxValue, rowCount: long.MaxValue, targetRows: 250_000);
        var seen = 0;
        var previousTo = long.MinValue;
        while (seen < 200 && planner.TryNext(out var a, out var b))
        {
            Assert.Equal(previousTo, a);
            Assert.True(b > a);
            previousTo = b;
            Assert.True(planner.Accept(countedRows: 0));
            seen++;
        }

        Assert.True(seen < 200, "an empty range of that size must be crossed in a logarithmic number of slices");
        Assert.Equal(long.MaxValue, previousTo);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    public void RandomData_AlwaysPartitionsTheRange(int seed)
    {
        var random = new Random(seed);
        var count = random.Next(1, 20_000);
        var to = (long)random.Next(1, 1_000_000);
        var instants = Enumerable.Range(0, count).Select(_ => random.NextInt64(0, to)).OrderBy(t => t).ToArray();
        var target = random.Next(1, 500);

        var outcome = Run(0, to, instants, target);

        AssertPartition(outcome, 0, to, instants.Length, target);
    }

    [Fact]
    public void InvalidArguments_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportSlicePlanner(10, 9, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportSlicePlanner(0, 10, 1, 0));
    }
}
