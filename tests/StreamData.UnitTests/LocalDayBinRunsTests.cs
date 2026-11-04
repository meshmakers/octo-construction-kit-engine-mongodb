using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7360: the local-day bin axis is cut into runs of equal bin width; each run becomes one <c>DATE_BIN</c> of the SQL
/// bin key, so the runs must reproduce the axis of <see cref="DownsamplingBinAxis"/> exactly.
/// </summary>
public class LocalDayBinRunsTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    private static (IReadOnlyList<DateTime> Starts, DateTime End) Axis(DateTime from, DateTime to, TimeZoneInfo? zone,
        TimeSpan origin, int days = 1)
    {
        var calendar = new BucketCalendar(BucketAlignment.CalendarDay, TimeSpan.FromDays(1), zone, origin);
        var starts = DownsamplingBinAxis.Build(from, to, calendar, days);
        var end = starts[^1];
        for (var i = 0; i < days; i++)
        {
            end = BucketBoundary.NextBucketEnd(end, calendar);
        }

        return (starts, end);
    }

    /// <summary>Every bin start of the axis, recomputed from the runs the way the SQL does it.</summary>
    private static List<DateTime> Expand(IReadOnlyList<LocalDayBinRun> runs)
    {
        var bins = new List<DateTime>();
        foreach (var run in runs)
        {
            for (var t = run.Start; t < run.End; t = t.AddSeconds(run.WidthSeconds))
            {
                bins.Add(t);
            }
        }

        return bins;
    }

    [Fact]
    public void AYearOfGasDaysInVienna_HasFiveRuns_TheShortAndTheLongDayAlone()
    {
        var from = new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc);
        var (starts, end) = Axis(from, from.AddYears(1), Vienna, TimeSpan.FromHours(6));

        var runs = LocalDayBinRuns.Build(starts, end);

        Assert.Equal([86_400L, 82_800L, 86_400L, 90_000L, 86_400L], runs.Select(r => r.WidthSeconds));
        Assert.Equal(new DateTime(2026, 3, 28, 5, 0, 0, DateTimeKind.Utc), runs[1].Start); // gas day of 28 March: 23 h
        Assert.Equal(new DateTime(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc), runs[3].Start); // gas day of 24 October: 25 h
        Assert.Equal(starts, Expand(runs));
        Assert.Equal(end, runs[^1].End);
    }

    [Fact]
    public void UtcDays_AreOneRun()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (starts, end) = Axis(from, from.AddYears(1), null, TimeSpan.Zero);

        var run = Assert.Single(LocalDayBinRuns.Build(starts, end));

        Assert.Equal((from, from.AddYears(1), 86_400L), (run.Start, run.End, run.WidthSeconds));
    }

    [Fact]
    public void SevenDayBinsAcrossTheSpringChange_HaveOneShortBin()
    {
        var from = new DateTime(2026, 3, 1, 23, 0, 0, DateTimeKind.Utc); // 2 March 00:00 Vienna
        var (starts, end) = Axis(from, from.AddDays(56), Vienna, TimeSpan.Zero, days: 7);

        var runs = LocalDayBinRuns.Build(starts, end);

        Assert.Contains(runs, r => r.WidthSeconds == 7 * 86_400 - 3600);
        Assert.Equal(starts, Expand(runs));
    }

    [Fact]
    public void NoBins_NoRuns() => Assert.Empty(LocalDayBinRuns.Build([], DateTime.UtcNow));

    [Fact]
    public void ABinThatIsNotWholeSeconds_IsRefused()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentException>(() => LocalDayBinRuns.Build([start], start.AddMilliseconds(1500)));
    }

    [Fact]
    public void TheBinOriginAndTheSourceRangeOfOneInstant_RenderTheSameLiteral_WhateverItsKind()
    {
        // PR #226 review: the bin literals and the source filter normalise an instant the same way (ToUniversalTime).
        var utcStart = new DateTime(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc);
        foreach (var start in new[] { utcStart, utcStart.ToLocalTime(), DateTime.SpecifyKind(utcStart, DateTimeKind.Unspecified) })
        {
            var q = new CrateQueryBuilder("t").AddRawAggregationExpression("COUNT(d.\"timestamp\")", "c")
                .WithDownsamplingByLocalDays([start], start.AddDays(1));

            var sql = new CrateQueryCompiler().CompileQuery(q);

            var origin = Between(sql, "d.\"timestamp\", '", "'::TIMESTAMP");
            Assert.Equal(origin, Between(sql, "d.\"timestamp\" >= '", "'"));
            Assert.Equal(Between(sql, "d.\"timestamp\" < '", "'"), Between(sql, "d.\"timestamp\" <= '", "'"));
        }
    }

    private static string Between(string text, string before, string after)
    {
        var start = text.IndexOf(before, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{before}' not in {text}");
        start += before.Length;
        return text[start..text.IndexOf(after, start, StringComparison.Ordinal)];
    }
}
