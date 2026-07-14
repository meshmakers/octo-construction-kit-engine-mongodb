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
    /// <para>
    /// <b>Bounded retro reach (AB#4196).</b> When <paramref name="maxRetroactiveReach"/> is non-null,
    /// the automatic dirty window is floored at <c>consumedWatermark - maxRetroactiveReach</c>: only
    /// the in-reach tail schedules a recompute. A batch whose <i>every</i> retroactive timestamp is
    /// older than that floor produces <b>no</b> window (returns <c>false</c>) — the change is out of
    /// automatic reach and left to a manual, unbounded <c>recomputeArchive</c>. Either way, when any
    /// retroactive timestamp falls before the floor, <paramref name="reachCapped"/> is set so the
    /// caller can audit the dropped tail. A <c>null</c> cap preserves the pre-1.6.8 unbounded
    /// behaviour.
    /// </para>
    /// </summary>
    public static bool TryBuildDirtyWindow(
        DateTime? consumedWatermark,
        IEnumerable<DateTime> timestamps,
        RecomputeChangeSource source,
        DateTime detectedAt,
        TimeSpan? maxRetroactiveReach,
        out ArchiveDirtyWindow window,
        out bool reachCapped)
    {
        window = null!;
        reachCapped = false;
        if (consumedWatermark is not { } watermark)
        {
            return false;
        }

        // The floor before which an automatic recompute is not scheduled. DateTime.MinValue ⇒ the
        // cap is either null (unbounded) or so large the subtraction would underflow — treat as no
        // floor. Subtract defensively so a pathological cap can never throw.
        var floor = DateTime.MinValue;
        if (maxRetroactiveReach is { } reach && reach > TimeSpan.Zero
            && watermark - DateTime.MinValue > reach)
        {
            floor = watermark - reach;
        }

        DateTime? earliest = null;
        DateTime? latest = null;
        foreach (var ts in timestamps)
        {
            if (ts >= watermark)
            {
                continue; // forward write relative to the consumed frontier — not retroactive
            }

            if (ts < floor)
            {
                // Retroactive, but older than the automatic reach cap — dropped from the automatic
                // window; the operator's manual recomputeArchive remains the unbounded escape hatch.
                reachCapped = true;
                continue;
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
