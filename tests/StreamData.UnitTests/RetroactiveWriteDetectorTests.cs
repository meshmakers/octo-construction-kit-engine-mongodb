using System;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RetroactiveWriteDetectorTests
{
    private static DateTime Utc(int h, int min = 0) => new(2026, 5, 11, h, min, 0, DateTimeKind.Utc);
    private static readonly DateTime DetectedAt = new(2026, 5, 11, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NullWatermark_NoDependentConsumedYet_NotRetroactive()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: null,
            new[] { Utc(8), Utc(9) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: null, out _, out var capped);

        Assert.False(result);
        Assert.False(capped);
    }

    [Fact]
    public void AllTimestampsAtOrAfterWatermark_ForwardAppend_NotRetroactive()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(10), Utc(11), Utc(12) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: null, out _, out var capped);

        Assert.False(result);
        Assert.False(capped);
    }

    [Fact]
    public void TimestampBeforeWatermark_Retroactive_BuildsWindow()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(8, 15), Utc(9, 45) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: null, out var window, out var capped);

        Assert.True(result);
        Assert.False(capped);
        Assert.Equal(Utc(8, 15), window.WindowStart);
        Assert.Equal(Utc(9, 45).AddTicks(1), window.WindowEnd);
        Assert.Equal(RecomputeChangeKind.RetroactiveModify, window.ChangeKind);
        Assert.Equal(RecomputeChangeSource.Pipeline, window.Source);
        Assert.Equal(DetectedAt, window.DetectedAt);
    }

    [Fact]
    public void MixedBatch_WindowCoversOnlyRetroactivePoints()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(9), Utc(11), Utc(8, 30), Utc(15) },
            RecomputeChangeSource.Import, DetectedAt, maxRetroactiveReach: null, out var window, out var capped);

        Assert.True(result);
        Assert.False(capped);
        Assert.Equal(Utc(8, 30), window.WindowStart);   // earliest retroactive
        Assert.Equal(Utc(9).AddTicks(1), window.WindowEnd); // latest retroactive (+1 tick)
        Assert.Equal(RecomputeChangeSource.Import, window.Source);
    }

    [Fact]
    public void SingleRetroactivePoint_WindowIsOneTickWide()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(9, 30) },
            RecomputeChangeSource.Manual, DetectedAt, maxRetroactiveReach: null, out var window, out var capped);

        Assert.True(result);
        Assert.False(capped);
        Assert.Equal(Utc(9, 30), window.WindowStart);
        Assert.Equal(Utc(9, 30).AddTicks(1), window.WindowEnd);
    }

    // ---------- Bounded retro reach (AB#4196) ----------

    [Fact]
    public void CapWithinReach_UnchangedWindow_NotCapped()
    {
        // Watermark 10:00, cap 2h ⇒ floor 08:00. Both points are at/after the floor: window unchanged.
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(8, 30), Utc(9, 45) },
            RecomputeChangeSource.Pipeline, DetectedAt,
            maxRetroactiveReach: TimeSpan.FromHours(2), out var window, out var capped);

        Assert.True(result);
        Assert.False(capped);
        Assert.Equal(Utc(8, 30), window.WindowStart);
        Assert.Equal(Utc(9, 45).AddTicks(1), window.WindowEnd);
    }

    [Fact]
    public void CapFloorsEarliest_PartiallyBeyond_WindowFlooredAndCapped()
    {
        // Watermark 10:00, cap 2h ⇒ floor 08:00. 07:00 is beyond the floor (dropped); 09:30 is in reach.
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(7), Utc(9, 30) },
            RecomputeChangeSource.Pipeline, DetectedAt,
            maxRetroactiveReach: TimeSpan.FromHours(2), out var window, out var capped);

        Assert.True(result);
        Assert.True(capped);                              // the 07:00 tail was dropped
        Assert.Equal(Utc(9, 30), window.WindowStart);     // only the in-reach point survives
        Assert.Equal(Utc(9, 30).AddTicks(1), window.WindowEnd);
    }

    [Fact]
    public void CapDropsEntireBatch_AllBeyondFloor_NoWindowButCapped()
    {
        // Watermark 10:00, cap 1h ⇒ floor 09:00. Both retroactive points are older than the floor.
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(7), Utc(8) },
            RecomputeChangeSource.Import, DetectedAt,
            maxRetroactiveReach: TimeSpan.FromHours(1), out var window, out var capped);

        Assert.False(result);   // nothing within automatic reach
        Assert.True(capped);    // but the operator is signalled that a deeper tail exists
        Assert.Null(window);
    }

    [Fact]
    public void NullCap_PreservesUnboundedBehaviour()
    {
        // A very deep retroactive point still builds a window when the cap is null.
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: null, out var window, out var capped);

        Assert.True(result);
        Assert.False(capped);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), window.WindowStart);
    }

    [Fact]
    public void ForwardWrite_NeverCapped_EvenWithCapSet()
    {
        // Forward-only batch: the cap is irrelevant, nothing retroactive, nothing capped.
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(10), Utc(11) },
            RecomputeChangeSource.Pipeline, DetectedAt,
            maxRetroactiveReach: TimeSpan.FromMinutes(1), out _, out var capped);

        Assert.False(result);
        Assert.False(capped);
    }
}
