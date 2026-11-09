using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using MongoDB.Bson;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8621: a late delivery whose latest value sits on a bucket boundary (every hourly value is stamped on the hour)
/// must recompute the bucket that holds that value. The dirty window is persisted in MongoDB at millisecond
/// resolution, so these tests send it through the same BSON date conversion before the planner aligns it to the
/// dependent's buckets - the path the recompute orchestrator takes. Before the fix the window end was
/// <c>latest + 1 tick</c>, the store cut it back to <c>latest</c>, and the last bucket of the delivery was left out:
/// on test-2 and staging-1 (tenant voestalpine) the hourly rollup missed exactly the last hour before a pause in the
/// raw data, and no later delivery repaired it.
/// </summary>
public class RetroactiveWriteDetectorBucketBoundaryTests
{
    private static readonly BucketCalendar Hourly = new(BucketAlignment.FixedSize, TimeSpan.FromHours(1));
    private static readonly BucketCalendar QuarterHourly = new(BucketAlignment.FixedSize, TimeSpan.FromMinutes(15));

    /// <summary>The conversion the MongoDB driver applies to a <see cref="DateTime" /> record member.</summary>
    private static DateTime ThroughStore(DateTime value) => new BsonDateTime(value).ToUniversalTime();

    private static ArchiveDirtyWindow ThroughStore(ArchiveDirtyWindow window) => window with
    {
        WindowStart = ThroughStore(window.WindowStart),
        WindowEnd = ThroughStore(window.WindowEnd),
    };

    /// <summary>One gas day of hourly values, 12.10. 04:00Z (06:00 Vienna) to 13.10. 03:00Z, stamped at the hour start.</summary>
    private static List<DateTime> GasDayHourlyValues() =>
        Enumerable.Range(0, 24)
            .Select(h => new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc).AddHours(h))
            .ToList();

    [Fact]
    public void LateDelivery_AfterAPauseOfSeveralDays_RecomputesTheLastBucketBeforeThePause()
    {
        var values = GasDayHourlyValues();
        // The delivery arrives three days after the gas day; the hourly rollup closed every bucket of that day long
        // ago (by the clock, without data) and no further data follows.
        var hourlyWatermark = new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

        Assert.True(RetroactiveWriteDetector.TryBuildDirtyWindow(
            hourlyWatermark, values, RecomputeChangeSource.Pipeline, hourlyWatermark, maxRetroactiveReach: null,
            out var window, out _));

        var stored = ThroughStore(window);
        var (start, end) = RecomputePlanner.AlignRangeToBuckets(stored.WindowStart, stored.WindowEnd, Hourly);

        Assert.Equal(values[0], start);
        Assert.Equal(values[^1].AddHours(1), end); // 13.10. 04:00Z: the bucket [03:00Z, 04:00Z) is included
        Assert.Equal(24, (int)(end - start).TotalHours);
    }

    [Fact]
    public void LateDelivery_WindowEnd_SurvivesTheStoreUnchanged()
    {
        var values = GasDayHourlyValues();

        Assert.True(RetroactiveWriteDetector.TryBuildDirtyWindow(
            values[^1].AddDays(5), values, RecomputeChangeSource.Pipeline, values[^1].AddDays(5),
            maxRetroactiveReach: null, out var window, out _));

        Assert.Equal(window.WindowEnd, ThroughStore(window.WindowEnd));
        Assert.True(ThroughStore(window.WindowEnd) > values[^1]);
    }

    [Fact]
    public void LateDelivery_OfQuarterHourValues_RecomputesTheLastBucketForEveryGranularity()
    {
        var first = new DateTime(2026, 10, 29, 23, 0, 0, DateTimeKind.Utc);
        var values = Enumerable.Range(0, 24).Select(i => first.AddMinutes(15 * i)).ToList(); // last: 04:45Z

        Assert.True(RetroactiveWriteDetector.TryBuildDirtyWindow(
            new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc), values, RecomputeChangeSource.Pipeline,
            new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc), maxRetroactiveReach: null, out var window, out _));

        var stored = ThroughStore(window);

        Assert.Equal(values[^1].AddMinutes(15),
            RecomputePlanner.AlignRangeToBuckets(stored.WindowStart, stored.WindowEnd, QuarterHourly).End);
        Assert.Equal(new DateTime(2026, 10, 30, 5, 0, 0, DateTimeKind.Utc),
            RecomputePlanner.AlignRangeToBuckets(stored.WindowStart, stored.WindowEnd, Hourly).End);
    }

    [Fact]
    public void LateDelivery_OnABoundary_DoesNotDragTheFollowingBucketIn()
    {
        var values = GasDayHourlyValues();

        Assert.True(RetroactiveWriteDetector.TryBuildDirtyWindow(
            values[^1].AddDays(1), values, RecomputeChangeSource.Pipeline, values[^1].AddDays(1),
            maxRetroactiveReach: null, out var window, out _));

        var stored = ThroughStore(window);
        var (_, end) = RecomputePlanner.AlignRangeToBuckets(stored.WindowStart, stored.WindowEnd, QuarterHourly);

        // The latest value is 03:00Z: its 15-minute bucket ends 03:15Z, the bucket after it is not recomputed.
        Assert.Equal(values[^1].AddMinutes(15), end);
    }

    [Fact]
    public void ManualCorrection_OfSeveralHours_RecomputesTheBucketOfTheLastCorrectedHour()
    {
        var corrected = new[]
        {
            new DateTime(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 30, 4, 0, 0, DateTimeKind.Utc),
        };

        var window = Assert.Single(RetroactiveWriteDetector.BuildManualDirtyWindows(
            new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), corrected, DateTime.UtcNow));

        var stored = ThroughStore(window);
        var (start, end) = RecomputePlanner.AlignRangeToBuckets(stored.WindowStart, stored.WindowEnd, Hourly);

        Assert.Equal(corrected[0], start);
        Assert.Equal(corrected[^1].AddHours(1), end);
    }

    [Fact]
    public void LatestTimestampWithSubMillisecondTicks_StaysInsideTheStoredWindow()
    {
        var latest = new DateTime(2026, 10, 30, 3, 59, 59, 999, DateTimeKind.Utc).AddTicks(5000);

        var end = RetroactiveWriteDetector.ExclusiveEndAfter(latest);

        Assert.Equal(new DateTime(2026, 10, 30, 4, 0, 0, DateTimeKind.Utc), end);
        Assert.True(ThroughStore(end) > ThroughStore(latest));
        Assert.Equal(end, ThroughStore(end));
        Assert.Equal(DateTimeKind.Utc, end.Kind);
    }

    [Fact]
    public void OldWindowEnd_OneTickPastTheLatest_LosesTheLastBucketInTheStore()
    {
        // Documents the defect this class guards: a tick is below the stored resolution.
        var latest = new DateTime(2026, 10, 13, 3, 0, 0, DateTimeKind.Utc);
        var storedEnd = ThroughStore(latest.AddTicks(1));

        Assert.Equal(latest, storedEnd);
        Assert.Equal(latest, RecomputePlanner.AlignRangeToBuckets(latest.AddHours(-23), storedEnd, Hourly).End);
    }
}
