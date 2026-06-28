using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Enumerates the buckets covered by a bucket-aligned recompute range (AB#4184, Phase 3c). The
/// orchestrator's planner already snaps <c>[from, to)</c> to the dependent's bucket boundaries, so
/// this walks from <c>from</c> to <c>to</c> one bucket at a time, yielding each
/// <c>[bucketStart, bucketEnd)</c> the recompute aggregates into staging. Mirrors the boundary
/// arithmetic of the engine's <c>BucketBoundary</c> (which is engine-internal and not visible here).
/// </summary>
internal static class RecomputeBucketEnumerator
{
    private const int RunawayGuard = 1_000_000;

    /// <summary>
    /// Yields the <c>[Start, End)</c> bucket intervals tiling <c>[from, to)</c>. Returns nothing for
    /// an empty or inverted range, or for a non-positive fixed bucket size.
    /// </summary>
    public static IEnumerable<(DateTime Start, DateTime End)> Enumerate(
        DateTime from, DateTime to, BucketAlignment alignment, TimeSpan bucketSize)
    {
        var start = AsUtc(from);
        var endBound = AsUtc(to);

        var iterations = 0;
        while (start < endBound)
        {
            var end = NextBucketEnd(start, alignment, bucketSize);
            if (end <= start)
            {
                yield break; // defensive: zero / negative bucket would loop forever
            }

            yield return (start, end);
            start = end;

            if (++iterations > RunawayGuard)
            {
                yield break;
            }
        }
    }

    private static DateTime NextBucketEnd(DateTime start, BucketAlignment alignment, TimeSpan bucketSize) =>
        alignment switch
        {
            BucketAlignment.FixedSize => start + bucketSize,
            BucketAlignment.CalendarDay => start.AddDays(1),
            BucketAlignment.Iso8601Week => start.AddDays(7),
            BucketAlignment.CalendarMonth => start.AddMonths(1),
            BucketAlignment.CalendarYear => start.AddYears(1),
            _ => start + bucketSize,
        };

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
