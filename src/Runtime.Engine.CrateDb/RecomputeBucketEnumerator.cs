using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Enumerates the buckets covered by a bucket-aligned recompute range (AB#4184, Phase 3c). The
/// orchestrator's planner already snaps <c>[from, to)</c> to the dependent's bucket boundaries, so
/// this walks from <c>from</c> to <c>to</c> one bucket at a time, yielding each
/// <c>[bucketStart, bucketEnd)</c> the recompute aggregates into staging. Delegates the boundary
/// arithmetic to the engine's <see cref="BucketBoundary"/> so calendar alignments honour the
/// reference time-zone (AB#4300 / O6) — the previous local UTC-only copy produced UTC calendar
/// buckets even when a rollup declared <c>ReferenceTimeZone</c>.
/// </summary>
internal static class RecomputeBucketEnumerator
{
    private const int RunawayGuard = 1_000_000;

    /// <summary>
    /// Yields the <c>[Start, End)</c> bucket intervals tiling <c>[from, to)</c>. Returns nothing for
    /// an empty or inverted range, or for a non-positive fixed bucket size. <paramref name="zone"/>
    /// (from the rollup's <c>ReferenceTimeZone</c>) aligns calendar buckets to local wall-clock
    /// boundaries; <c>null</c> keeps UTC calendar boundaries.
    /// </summary>
    public static IEnumerable<(DateTime Start, DateTime End)> Enumerate(
        DateTime from, DateTime to, BucketAlignment alignment, TimeSpan bucketSize, TimeZoneInfo? zone = null)
    {
        var start = AsUtc(from);
        var endBound = AsUtc(to);

        var iterations = 0;
        while (start < endBound)
        {
            var end = BucketBoundary.NextBucketEnd(start, alignment, bucketSize, zone);
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

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
