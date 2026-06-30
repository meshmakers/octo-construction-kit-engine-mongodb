using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pure detection of a retroactive archive write (AB#4184, Information A): a write whose timestamp
/// falls <b>strictly before</b> the high-water mark that dependent rollups have already consumed is
/// a correction / late value, not a forward append, and therefore makes those dependents stale. Given
/// the consumed watermark and a batch of write timestamps, builds the covering
/// <see cref="ArchiveDirtyWindow"/> if any timestamp is retroactive.
/// </summary>
internal static class RetroactiveWriteDetector
{
    /// <summary>
    /// Returns <c>true</c> and the covering dirty window when at least one timestamp is strictly
    /// before <paramref name="consumedWatermark"/>. The window spans the earliest..latest retroactive
    /// timestamp; <see cref="ArchiveDirtyWindow.WindowEnd"/> is one tick past the latest so the
    /// planner's bucket alignment always includes the latest point's own bucket. Returns
    /// <c>false</c> when the watermark is null (no dependent has consumed anything yet) or no
    /// timestamp is retroactive.
    /// </summary>
    public static bool TryBuildDirtyWindow(
        DateTime? consumedWatermark,
        IEnumerable<DateTime> timestamps,
        RecomputeChangeSource source,
        DateTime detectedAt,
        out ArchiveDirtyWindow window)
    {
        window = null!;
        if (consumedWatermark is not { } watermark)
        {
            return false;
        }

        DateTime? earliest = null;
        DateTime? latest = null;
        foreach (var ts in timestamps)
        {
            if (ts >= watermark)
            {
                continue; // forward write relative to the consumed frontier — not retroactive
            }

            if (earliest is null || ts < earliest)
            {
                earliest = ts;
            }
            if (latest is null || ts > latest)
            {
                latest = ts;
            }
        }

        if (earliest is null)
        {
            return false;
        }

        window = new ArchiveDirtyWindow(
            earliest.Value,
            latest!.Value.AddTicks(1),
            RecomputeChangeKind.RetroactiveModify,
            source,
            detectedAt);
        return true;
    }
}
