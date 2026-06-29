using System;
using System.Linq;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
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
}
