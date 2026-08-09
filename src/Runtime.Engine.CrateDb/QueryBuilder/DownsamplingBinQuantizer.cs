using System;

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
/// The windowed path assumes the query origin (<c>From</c>) sits on a source-grain boundary, which
/// holds for every MeshBoard time selection (calendar periods and midnight-aligned custom ranges).
/// A caller that downsamples a windowed archive from an arbitrary sub-grain instant would still see
/// the boundary bins straddle; that is out of scope here and unchanged from prior behaviour.
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
}
