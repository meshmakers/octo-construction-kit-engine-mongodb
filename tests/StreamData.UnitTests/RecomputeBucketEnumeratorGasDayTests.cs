using System;
using System.Linq;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>AB#7359: a recompute walks the gas days (06:00 Europe/Vienna) of the rollup's <see cref="BucketCalendar"/>.</summary>
public class RecomputeBucketEnumeratorGasDayTests
{
    private static readonly BucketCalendar GasDay = new(
        BucketAlignment.CalendarDay, TimeSpan.FromDays(1), TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"), TimeSpan.FromHours(6));

    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GasDays_AcrossTheSpringChange_Are24_23_24Hours()
    {
        var buckets = RecomputeBucketEnumerator.Enumerate(Utc(2026, 3, 27, 5), Utc(2026, 3, 30, 4), GasDay).ToList();

        Assert.Equal(
            [(Utc(2026, 3, 27, 5), Utc(2026, 3, 28, 5)), (Utc(2026, 3, 28, 5), Utc(2026, 3, 29, 4)), (Utc(2026, 3, 29, 4), Utc(2026, 3, 30, 4))],
            buckets);
        Assert.Equal([24, 23, 24], buckets.Select(b => (int)(b.End - b.Start).TotalHours));
    }

    [Fact]
    public void GasDays_AcrossTheAutumnChange_Are24_25_24Hours()
    {
        var buckets = RecomputeBucketEnumerator.Enumerate(Utc(2026, 10, 23, 4), Utc(2026, 10, 26, 5), GasDay).ToList();

        Assert.Equal([24, 25, 24], buckets.Select(b => (int)(b.End - b.Start).TotalHours));
    }

    [Fact]
    public void WithoutAnOffset_TheCalendarOverloadEqualsTheZoneOverload()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        var midnight = new BucketCalendar(BucketAlignment.CalendarDay, TimeSpan.FromDays(1), zone);

        Assert.Equal(
            RecomputeBucketEnumerator.Enumerate(Utc(2026, 3, 27, 23), Utc(2026, 3, 31), BucketAlignment.CalendarDay, TimeSpan.FromDays(1), zone),
            RecomputeBucketEnumerator.Enumerate(Utc(2026, 3, 27, 23), Utc(2026, 3, 31), midnight));
    }
}
