using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// One stretch of a local-day bin axis in which every bin has the same width (AB#7360). Inside it the bin of an
/// instant <c>t</c> is <c>DATE_BIN(Width, t, Start)</c>; the stretch covers <c>[Start, End)</c>.
/// </summary>
/// <param name="Start">UTC start of the first bin of the run.</param>
/// <param name="End">UTC end of the last bin of the run (exclusive).</param>
/// <param name="WidthSeconds">Width of every bin of the run in whole seconds.</param>
internal readonly record struct LocalDayBinRun(DateTime Start, DateTime End, long WidthSeconds);

/// <summary>
/// Cuts the bin axis of a local-day downsampling query into runs of equal bin width (AB#7360). The axis comes from
/// the engine's <see cref="DownsamplingBinAxis"/> (<see cref="BucketBoundary"/> on a <see cref="BucketCalendar"/>), so
/// its bins are 23, 24 or 25 hours across DST changes. The SQL needs a bin key per source row that matches those
/// instants exactly and stays O(source rows) without a join against a generated series (AB#4713): the key is one
/// <c>DATE_BIN</c> per run, chosen by a <c>CASE</c> over the run ends. A year in a zone with DST has five runs
/// (24 h, the 23 h day, 24 h, the 25 h day, 24 h), a zone without DST one. Pure, no I/O.
/// </summary>
internal static class LocalDayBinRuns
{
    /// <summary>
    /// The runs of the axis whose bins start at <paramref name="binStarts"/> and whose last bin ends at
    /// <paramref name="axisEnd"/>. Throws when the starts are not strictly ascending or a bin is not a whole number of
    /// seconds (a local-day bin always is).
    /// </summary>
    public static IReadOnlyList<LocalDayBinRun> Build(IReadOnlyList<DateTime> binStarts, DateTime axisEnd)
    {
        ArgumentNullException.ThrowIfNull(binStarts);
        var runs = new List<LocalDayBinRun>();
        if (binStarts.Count == 0)
        {
            return runs;
        }

        var runStart = binStarts[0];
        long runWidth = -1;
        for (var i = 0; i < binStarts.Count; i++)
        {
            var start = binStarts[i];
            var end = i + 1 < binStarts.Count ? binStarts[i + 1] : axisEnd;
            var width = WidthSeconds(start, end);
            if (runWidth < 0)
            {
                runWidth = width;
            }
            else if (width != runWidth)
            {
                runs.Add(new LocalDayBinRun(runStart, start, runWidth));
                runStart = start;
                runWidth = width;
            }
        }

        runs.Add(new LocalDayBinRun(runStart, axisEnd, runWidth));
        return runs;
    }

    private static long WidthSeconds(DateTime start, DateTime end)
    {
        var ticks = end.Ticks - start.Ticks;
        if (ticks <= 0 || ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                $"A local-day bin must end after its start and last a whole number of seconds ({start:O} to {end:O}).");
        }

        return ticks / TimeSpan.TicksPerSecond;
    }
}
