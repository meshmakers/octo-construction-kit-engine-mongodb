using System;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// Pure helper that decides how many output bins a downsampling query should produce, given the
/// requested (pixel-driven) bucket count and the number of distinct source bins actually present in
/// the range. No I/O — deterministic and unit-testable (like <c>BucketBoundary</c>).
/// </summary>
/// <remarks>
/// <para>
/// Two clamps live here, both closing the same failure mode from opposite sides:
/// </para>
/// <list type="number">
/// <item>
/// <b>Raw archives (point-in-time).</b> A request finer than the data (more bins than distinct
/// timestamps) only yields sparse, mostly-empty bins, so the count is clamped down to the distinct
/// bin count. This is the original AB#4246 clamp.
/// </item>
/// <item>
/// <b>Windowed archives (rollup / time-range).</b> Here the bin width must additionally be an
/// <em>integer multiple of the source grain</em> and aligned to grain boundaries, or the §7
/// fully-contained predicate drops every source window that straddles a bin edge. The distinct
/// source-bin count (<c>COUNT(DISTINCT window_start)</c>) is exactly the number of source grain
/// windows in range, so quantizing the output to <c>round(distinctBins / merge)</c> — where
/// <c>merge</c> whole source windows fall in one output bin — guarantees each output bin covers a
/// whole number of source windows. Without this a request just below the distinct count
/// (e.g. 670 pixels over 720 hourly windows) produced a 1.07 h bin that was neither the grain nor a
/// multiple of it, so ~94 % of the hourly windows were dropped and a month chart read ~6 % of the
/// true sum (AB#4714 local repro). Merge = 1 (the common case) means "read every source window at
/// native grain", which is lossless.
/// </item>
/// </list>
/// <para>
/// The windowed path needs the bin axis to start on a source-grain boundary, or the boundary bins
/// straddle two source windows and the §7 predicate drops them. <see cref="QuantizeToGrain"/>
/// therefore snaps the origin down to the grain itself instead of assuming the caller did
/// (AB#5157 review) — a no-op for every already-aligned window, so the axis of a calendar-period or
/// midnight-aligned selection is unchanged.
/// </para>
/// </remarks>
internal static class DownsamplingBinQuantizer
{
    /// <summary>
    /// Computes the effective output bin count.
    /// </summary>
    /// <param name="requestedLimit">The caller-requested bucket count (pixel-driven). Must be &gt; 0.</param>
    /// <param name="distinctSourceBins">
    /// The number of distinct source bins in range (raw: distinct timestamps; windowed: distinct
    /// <c>window_start</c> values). A non-positive value means the probe found nothing / failed, in
    /// which case the requested limit is returned unchanged.
    /// </param>
    /// <param name="isWindowed">True for rollup / time-range archives (windowed storage).</param>
    /// <returns>The bucket count to pass to the downsampling query.</returns>
    public static int Quantize(int requestedLimit, int distinctSourceBins, bool isWindowed)
    {
        if (requestedLimit <= 0 || distinctSourceBins <= 0)
        {
            return requestedLimit;
        }

        if (!isWindowed)
        {
            // Raw: clamp down only. A finer request just yields empty bins; a coarser one is fine.
            return distinctSourceBins < requestedLimit ? distinctSourceBins : requestedLimit;
        }

        // Windowed: quantize so each output bin merges a whole number of source grain windows.
        // merge = how many source windows per output bin, chosen to land nearest the request.
        var merge = Math.Max(1,
            (int)Math.Round((double)distinctSourceBins / requestedLimit, MidpointRounding.AwayFromZero));
        return Math.Max(1,
            (int)Math.Round((double)distinctSourceBins / merge, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Windowed-archive quantization against the <em>declared</em> grain (AB#4817). Returns the bin
    /// geometry directly — a bin width that is an exact integer multiple of the grain plus the
    /// matching bucket count — instead of a bucket count the caller re-derives a width from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failure modes of the distinct-bin-count route (<see cref="Quantize"/>) motivated this:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <c>COUNT(DISTINCT window_start)</c> counts source windows <em>with data</em>, not grain slots
    /// in range. Any gap (event-driven series) makes the count fall short of the range's grain count,
    /// and the width re-derived from it (<c>range / effectiveLimit</c>) stops being a grain multiple
    /// — 288 five-minute slots with 3 empty ones yielded a 303 s bin whose 3 s/bin drift made the §7
    /// fully-contained predicate drop every window except where the axes happened to re-align
    /// (observed on prod-1 as "sensor data stopped hours ago").
    /// </item>
    /// <item>
    /// Even with a complete count, <c>round(range / effectiveLimit)</c> is only a grain multiple when
    /// the merge divides the count evenly (720 windows at merge 7 → 103 bins → 25 165 s ≠ 7 h).
    /// </item>
    /// </list>
    /// <para>
    /// Computing from the declared grain (<c>ArchiveSnapshot.Period</c> — a rollup's bucket size, a
    /// time-range archive's period) avoids both: no probe, no data dependence, and the width is a
    /// grain multiple by construction.
    /// </para>
    /// <para>
    /// The returned <c>Origin</c> is <paramref name="from"/> snapped down to the grain with the same
    /// <see cref="BucketBoundary"/> logic that wrote the stored boundaries, so every bin edge lands
    /// on a source window start and each bin covers whole source windows. It is the query's lower
    /// bound as well as the <c>DATE_BIN</c> origin: the source filter matches windows that OVERLAP
    /// the range, so a first bin starting before the requested instant would otherwise read only the
    /// part of its source windows that reaches into the range. Snapping is a no-op whenever
    /// <paramref name="from"/> already sits on the grain, which is every calendar-period and
    /// midnight-aligned selection — the axis of those queries does not move (AB#5157 review).
    /// </para>
    /// </remarks>
    /// <param name="requestedLimit">The caller-requested bucket count (pixel-driven). Must be &gt; 0.</param>
    /// <param name="from">Start of the requested range.</param>
    /// <param name="to">End of the requested range. Must be after <paramref name="from"/>.</param>
    /// <param name="grain">
    /// The archive's window length. Must be a positive whole number of seconds — the SQL interval
    /// literal and the caller-side bin axis both work in whole seconds.
    /// </param>
    /// <returns>
    /// The bucket count, bin width and axis origin to run the query with, or null when the inputs
    /// are out of contract (caller should fall back to the probe-based <see cref="Quantize"/> route).
    /// </returns>
    public static (int EffectiveLimit, int IntervalSeconds, DateTime Origin)? QuantizeToGrain(
        int requestedLimit, DateTime from, DateTime to, TimeSpan grain)
    {
        var range = to - from;
        if (requestedLimit <= 0 || range <= TimeSpan.Zero || grain <= TimeSpan.Zero)
        {
            return null;
        }

        var grainSeconds = grain.TotalSeconds;
        if (grainSeconds < 1 || grainSeconds % 1 != 0)
        {
            // Sub-second or fractional-second grains cannot be expressed on the whole-second bin
            // axis — let the caller fall back rather than silently mis-bin.
            return null;
        }

        // merge = whole source windows per output bin, chosen to land nearest the request.
        var requestedBinSeconds = range.TotalSeconds / requestedLimit;
        var merge = Math.Max(1L,
            (long)Math.Round(requestedBinSeconds / grainSeconds, MidpointRounding.AwayFromZero));
        var intervalSeconds = merge * (long)grainSeconds;
        if (intervalSeconds > int.MaxValue)
        {
            return null;
        }

        // Snap to the GRAIN, not to the (possibly merged) bin width: every bin edge then still lands
        // on a source window start, while an already-grain-aligned window keeps the exact axis it
        // has today. Aligning to the wider bin would move the axis of queries that work fine.
        var origin = BucketBoundary.AlignDown(from, BucketAlignment.FixedSize, grain);
        var effectiveLimit = (int)Math.Ceiling((to - origin).TotalSeconds / intervalSeconds);
        return (Math.Max(1, effectiveLimit), (int)intervalSeconds, origin);
    }
}
