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
            RecomputeChangeSource.Pipeline, DetectedAt, out _);

        Assert.False(result);
    }

    [Fact]
    public void AllTimestampsAtOrAfterWatermark_ForwardAppend_NotRetroactive()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(10), Utc(11), Utc(12) },
            RecomputeChangeSource.Pipeline, DetectedAt, out _);

        Assert.False(result);
    }

    [Fact]
    public void TimestampBeforeWatermark_Retroactive_BuildsWindow()
    {
        var result = RetroactiveWriteDetector.TryBuildDirtyWindow(
            consumedWatermark: Utc(10),
            new[] { Utc(8, 15), Utc(9, 45) },
            RecomputeChangeSource.Pipeline, DetectedAt, out var window);

        Assert.True(result);
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
            RecomputeChangeSource.Import, DetectedAt, out var window);

        Assert.True(result);
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
            RecomputeChangeSource.Manual, DetectedAt, out var window);

        Assert.True(result);
        Assert.Equal(Utc(9, 30), window.WindowStart);
        Assert.Equal(Utc(9, 30).AddTicks(1), window.WindowEnd);
    }
}
