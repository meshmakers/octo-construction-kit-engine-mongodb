using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// Pixel-driven bucket counts vs. the distinct source-bin count. The windowed path must always land
// on a whole-number merge of source grain windows so the §7 fully-contained predicate keeps every
// window (AB#4714). The raw path only clamps a too-fine request down.
public class DownsamplingBinQuantizerTests
{
    // --- The regression that motivated this: 670 pixels over 720 hourly windows ---------------

    [Fact]
    public void Windowed_RequestJustBelowDistinct_SnapsUpToNativeGrain()
    {
        // 670 requested, 720 distinct hourly windows: merge = round(720/670) = 1 → read every window
        // (720). Before the fix this stayed 670 → 1.07 h bins → ~94 % of windows dropped.
        var effective = DownsamplingBinQuantizer.Quantize(requestedLimit: 670, distinctSourceBins: 720, isWindowed: true);
        Assert.Equal(720, effective);
    }

    [Fact]
    public void Windowed_RequestFarBelowDistinct_MergesWholeWindows()
    {
        // 300 requested, 720 windows: merge = round(720/300) = 2 → 360 output bins, each = 2 windows.
        var effective = DownsamplingBinQuantizer.Quantize(300, 720, isWindowed: true);
        Assert.Equal(360, effective);
    }

    [Fact]
    public void Windowed_RequestFinerThanData_ClampsToDistinct()
    {
        // 670 requested, 30 daily windows: merge = max(1, round(30/670)) = 1 → 30 (one bin per day).
        var effective = DownsamplingBinQuantizer.Quantize(670, 30, isWindowed: true);
        Assert.Equal(30, effective);
    }

    [Theory]
    [InlineData(100, 720, 103)] // merge round(7.2)=7 → round(720/7) = 103
    [InlineData(200, 720, 180)] // merge round(3.6)=4 → round(720/4) = 180
    [InlineData(360, 720, 360)] // merge 2 → 360
    [InlineData(720, 720, 720)] // merge 1 → 720
    public void Windowed_StaysNearRequest_AndDividesWindowsWholely(int requested, int distinct, int expected)
    {
        // Guard: every result must merge a whole number of source windows (distinct / effective is an
        // integer-ish merge), so the bin width is an integer multiple of the grain.
        var effective = DownsamplingBinQuantizer.Quantize(requested, distinct, isWindowed: true);
        Assert.Equal(expected, effective);
    }

    // --- Raw archives: clamp-down only (AB#4246), never snap up ----------------------------------

    [Fact]
    public void Raw_RequestFinerThanData_ClampsDown()
    {
        Assert.Equal(50, DownsamplingBinQuantizer.Quantize(670, 50, isWindowed: false));
    }

    [Fact]
    public void Raw_RequestCoarserThanData_Unchanged()
    {
        // Raw bins finer than data are just sparse, not wrong — a coarser request is honoured as-is.
        Assert.Equal(300, DownsamplingBinQuantizer.Quantize(300, 720, isWindowed: false));
    }

    // --- Degenerate inputs ----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 720, true)]
    [InlineData(670, 0, true)]
    [InlineData(-5, 720, false)]
    [InlineData(670, -1, false)]
    public void NonPositiveInputs_ReturnRequestedUnchanged(int requested, int distinct, bool windowed)
    {
        Assert.Equal(requested, DownsamplingBinQuantizer.Quantize(requested, distinct, windowed));
    }
}
