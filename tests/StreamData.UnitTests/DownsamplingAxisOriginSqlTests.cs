using System;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

/// <summary>
/// The bin axis of a windowed (rollup) downsampling query has to start on a source window
/// boundary: the §7 predicate keeps a source window only when it is fully contained in its bin, so
/// an axis offset by a sub-grain amount makes every window straddle two bins and the whole chart
/// reads blank. Callers used to pre-align the query window to compensate, which they cannot do
/// correctly — neither the grain nor the chosen bin width is part of the query contract — so the
/// engine snaps its own origin (AB#5157 review). These pin the emitted geometry; the behaviour
/// itself is proven against a real CrateDB in the asset-repo integration suite
/// (TC_E2E_12_FixedSizeRung_DownsampledFromAnUnalignedWindow_ReadsLikeTheAlignedOne).
/// </summary>
public class DownsamplingAxisOriginSqlTests
{
    private const string Table = "\"meshtest\".\"archive_a1\"";
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    private static string CompileFor(DateTime from, DateTime to)
    {
        var geometry = DownsamplingBinQuantizer.QuantizeToGrain(600, from, to, Hourly);
        Assert.NotNull(geometry);
        var (limit, intervalSeconds, origin) = geometry!.Value;

        var builder = new CrateQueryBuilder(Table).UseWindowedTimeAxis();
        builder.WithTimeFilter(origin, to);
        builder.WithDownsampling(limit, origin, to, intervalSeconds);
        builder.AddVariable("timestamp", "T", null);
        builder.AddVariable("voltage_sum", null, AggregationFunctionDto.Sum);

        return new CrateQueryCompiler().CompileQuery(builder);
    }

    [Fact]
    public void SubGrainWindow_BinsFromTheGrainBoundary_NotFromTheRequestedInstant()
    {
        var from = new DateTime(2026, 3, 4, 0, 42, 54, DateTimeKind.Utc);

        var sql = CompileFor(from, from.AddHours(6));

        // DATE_BIN's origin — the axis anchor.
        Assert.Contains("'2026-03-04 00:00:00.000Z'::TIMESTAMP", sql);
        Assert.DoesNotContain("'2026-03-04 00:42:54.000Z'::TIMESTAMP", sql);
        // …and the source filter reads from the same instant, because the window filter matches
        // OVERLAPPING windows: a bin starting before the request would otherwise sum only the tail
        // of its source windows.
        Assert.Contains("\"window_end\" > '2026-03-04 00:00:00.000Z'", sql);
    }

    [Fact]
    public void AlreadyAlignedWindow_KeepsItsAxisExactly()
    {
        // The property that makes the change safe for every query that works today.
        var from = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);

        var sql = CompileFor(from, from.AddHours(6));

        Assert.Contains("'2026-03-04 00:00:00.000Z'::TIMESTAMP", sql);
        Assert.Contains("\"window_end\" > '2026-03-04 00:00:00.000Z'", sql);
    }
}
