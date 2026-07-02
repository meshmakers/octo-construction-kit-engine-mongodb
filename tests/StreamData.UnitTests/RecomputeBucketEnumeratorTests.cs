using System;
using System.Linq;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RecomputeBucketEnumeratorTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FixedSize_TilesRangeIntoHourlyBuckets()
    {
        var buckets = RecomputeBucketEnumerator
            .Enumerate(Utc(2026, 5, 11, 10), Utc(2026, 5, 11, 13), BucketAlignment.FixedSize, TimeSpan.FromHours(1))
            .ToList();

        Assert.Equal(3, buckets.Count);
        Assert.Equal((Utc(2026, 5, 11, 10), Utc(2026, 5, 11, 11)), buckets[0]);
        Assert.Equal((Utc(2026, 5, 11, 11), Utc(2026, 5, 11, 12)), buckets[1]);
        Assert.Equal((Utc(2026, 5, 11, 12), Utc(2026, 5, 11, 13)), buckets[2]);
    }

    [Fact]
    public void CalendarDay_TilesRangeIntoDailyBuckets()
    {
        var buckets = RecomputeBucketEnumerator
            .Enumerate(Utc(2026, 5, 11), Utc(2026, 5, 13), BucketAlignment.CalendarDay, TimeSpan.Zero)
            .ToList();

        Assert.Equal(2, buckets.Count);
        Assert.Equal((Utc(2026, 5, 11), Utc(2026, 5, 12)), buckets[0]);
        Assert.Equal((Utc(2026, 5, 12), Utc(2026, 5, 13)), buckets[1]);
    }

    [Fact]
    public void CalendarMonth_CrossesMonthBoundaryCorrectly()
    {
        var buckets = RecomputeBucketEnumerator
            .Enumerate(Utc(2026, 1, 1), Utc(2026, 3, 1), BucketAlignment.CalendarMonth, TimeSpan.Zero)
            .ToList();

        Assert.Equal(2, buckets.Count);
        Assert.Equal((Utc(2026, 1, 1), Utc(2026, 2, 1)), buckets[0]);
        Assert.Equal((Utc(2026, 2, 1), Utc(2026, 3, 1)), buckets[1]);
    }

    [Fact]
    public void EmptyRange_YieldsNothing()
    {
        Assert.Empty(RecomputeBucketEnumerator
            .Enumerate(Utc(2026, 5, 11, 10), Utc(2026, 5, 11, 10), BucketAlignment.FixedSize, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void NonPositiveFixedBucket_YieldsNothing()
    {
        Assert.Empty(RecomputeBucketEnumerator
            .Enumerate(Utc(2026, 5, 11, 10), Utc(2026, 5, 11, 13), BucketAlignment.FixedSize, TimeSpan.Zero));
    }

    // ---- AB#4282: calendar-aligned enumeration over a multi-year range must not overflow ----------
    // A calendar rollup's BucketSize (a TimeSpan derived from a >Int32 BucketSizeMs) is ignored by
    // the calendar branches, which walk via AddMonths / AddYears / AddDays on DateTime — pure
    // calendar arithmetic that never converts the range duration to Int32.

    [Fact]
    public void CalendarMonth_OverThreeYears_YieldsThirtySixBucketsWithoutOverflow()
    {
        // BucketSize deliberately set to the calendar-month width (2,419,200,000 ms > Int32.MaxValue)
        // to prove the enumeration ignores it and does not overflow on the range.
        var buckets = RecomputeBucketEnumerator
            .Enumerate(Utc(2023, 1, 1), Utc(2026, 1, 1), BucketAlignment.CalendarMonth,
                TimeSpan.FromMilliseconds(2_419_200_000L))
            .ToList();

        Assert.Equal(36, buckets.Count);
        Assert.Equal((Utc(2023, 1, 1), Utc(2023, 2, 1)), buckets[0]);
        Assert.Equal((Utc(2025, 12, 1), Utc(2026, 1, 1)), buckets[^1]);
    }

    [Fact]
    public void CalendarYear_OverMultiYearRange_YieldsOneBucketPerYear()
    {
        var buckets = RecomputeBucketEnumerator
            .Enumerate(Utc(2023, 1, 1), Utc(2026, 1, 1), BucketAlignment.CalendarYear,
                TimeSpan.FromMilliseconds(31_536_000_000L))
            .ToList();

        Assert.Equal(3, buckets.Count);
        Assert.Equal((Utc(2023, 1, 1), Utc(2024, 1, 1)), buckets[0]);
        Assert.Equal((Utc(2025, 1, 1), Utc(2026, 1, 1)), buckets[2]);
    }

    [Fact]
    public void Iso8601Week_OverThreeYears_TilesEveryWeekWithoutOverflow()
    {
        var buckets = RecomputeBucketEnumerator
            .Enumerate(Utc(2023, 1, 2), Utc(2026, 1, 5), BucketAlignment.Iso8601Week,
                TimeSpan.FromMilliseconds(604_800_000L))
            .ToList();

        // 2023-01-02 (Mon) → 2026-01-05 (Mon) is exactly 157 seven-day buckets.
        Assert.Equal(157, buckets.Count);
        Assert.Equal((Utc(2023, 1, 2), Utc(2023, 1, 9)), buckets[0]);
        Assert.All(buckets, b => Assert.Equal(TimeSpan.FromDays(7), b.End - b.Start));
    }

    // ---- AB#4300 / O6: calendar-day enumeration honours the reference time-zone ------------------
    // Regression coverage for the bug found on voestalpine — a CalendarDay + Europe/Vienna rollup
    // stored UTC-midnight buckets because the enumerator's boundary math was hardcoded UTC. It now
    // delegates to the zone-aware BucketBoundary, so day boundaries land on Vienna local midnight.

    [Fact]
    public void CalendarDay_WithReferenceZone_SnapsToViennaLocalMidnight()
    {
        var zone = BucketBoundary.ResolveZone("Europe/Vienna");
        Assert.NotNull(zone);

        // Start at Vienna local midnight of 2026-06-23 (CEST = UTC+2 → 2026-06-22 22:00:00Z).
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Unspecified), zone!);
        Assert.Equal(Utc(2026, 6, 22, 22), startUtc);

        var buckets = RecomputeBucketEnumerator
            .Enumerate(startUtc, startUtc.AddDays(2), BucketAlignment.CalendarDay, TimeSpan.Zero, zone)
            .ToList();

        Assert.Equal(2, buckets.Count);
        // The stored window boundaries are the UTC instants of Vienna midnight, NOT 00:00Z.
        Assert.Equal((Utc(2026, 6, 22, 22), Utc(2026, 6, 23, 22)), buckets[0]);
        Assert.Equal((Utc(2026, 6, 23, 22), Utc(2026, 6, 24, 22)), buckets[1]);
        // Each boundary is local midnight in Vienna; summer days are a full 24 h.
        Assert.All(buckets, b =>
        {
            Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTimeFromUtc(b.Start, zone!).TimeOfDay);
            Assert.Equal(TimeSpan.FromHours(24), b.End - b.Start);
        });
    }

    [Fact]
    public void CalendarDay_AcrossDstFallBack_YieldsTwentyFiveHourBucket()
    {
        var zone = BucketBoundary.ResolveZone("Europe/Vienna");
        Assert.NotNull(zone);

        // 2026-10-25 is the CEST→CET fall-back in Vienna, so that local day is 25 h long.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Unspecified), zone!);

        var buckets = RecomputeBucketEnumerator
            .Enumerate(startUtc, startUtc.AddDays(2), BucketAlignment.CalendarDay, TimeSpan.Zero, zone)
            .ToList();

        Assert.Equal(2, buckets.Count);
        Assert.Equal(TimeSpan.FromHours(25), buckets[0].End - buckets[0].Start); // the DST-long day
        Assert.Equal(TimeSpan.FromHours(24), buckets[1].End - buckets[1].Start); // the following normal day
        Assert.All(buckets, b =>
            Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTimeFromUtc(b.Start, zone!).TimeOfDay));
    }
}
