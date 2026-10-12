using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RollupRecomputeSqlBuilderTests
{
    private const string LiveTable = "\"acmecorp\".\"archive_rollup1\"";
    private const string StagingTable = "\"acmecorp\".\"archive_rollup1__rc\"";

    [Fact]
    public void StagingTable_IsLiveTableWithRcSuffix_InTenantSchema()
    {
        var staging = RollupRecomputeSqlBuilder.StagingTable("acmecorp", "rollup1");

        Assert.Contains("__rc", staging);
        Assert.Contains("archive_rollup1__rc", staging);
        Assert.StartsWith("\"acmecorp\".", staging);
    }

    [Fact]
    public void BuildDropIfExists_EmitsIfExists()
    {
        var sql = RollupRecomputeSqlBuilder.BuildDropIfExists(StagingTable);

        Assert.Equal($"DROP TABLE IF EXISTS {StagingTable};", sql);
    }

    private static readonly DateTime SliceFrom = new(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SliceTo = new(2026, 5, 11, 13, 0, 0, DateTimeKind.Utc);
    private static readonly long SliceFromMs = new DateTimeOffset(SliceFrom).ToUnixTimeMilliseconds();
    private static readonly long SliceToMs = new DateTimeOffset(SliceTo).ToUnixTimeMilliseconds();

    [Fact]
    public void BuildInsertFromStagingSlice_StampsGenerationLiteral_AndBoundsTheSliceOnBothSides()
    {
        var columns = new List<string> { "window_start", "window_end", "voltage_avg_sum" };

        var sql = RollupRecomputeSqlBuilder.BuildInsertFromStagingSlice(
            LiveTable, StagingTable, columns, 7, SliceFrom, SliceTo);

        Assert.Equal(
            $"INSERT INTO {LiveTable} (\"window_start\", \"window_end\", \"voltage_avg_sum\", \"generation\") " +
            $"SELECT \"window_start\", \"window_end\", \"voltage_avg_sum\", 7 FROM {StagingTable} " +
            $"WHERE \"window_start\" >= {SliceFromMs} AND \"window_start\" < {SliceToMs};",
            sql);
        Assert.DoesNotContain("'", sql);
    }

    [Fact]
    public void BuildInsertFromStagingSlice_EmptyColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RollupRecomputeSqlBuilder.BuildInsertFromStagingSlice(
                LiveTable, StagingTable, Array.Empty<string>(), 1, SliceFrom, SliceTo));
    }

    [Fact]
    public void BuildCountStaged_CountsTheRowsACopySliceWouldInsert()
    {
        var sql = RollupRecomputeSqlBuilder.BuildCountStaged(StagingTable, SliceFrom, SliceTo);

        Assert.Equal(
            $"SELECT COUNT(*) FROM {StagingTable} " +
            $"WHERE \"window_start\" >= {SliceFromMs} AND \"window_start\" < {SliceToMs};",
            sql);
    }

    [Fact]
    public void BuildDeleteAllStaged_HasNoPredicate()
    {
        Assert.Equal($"DELETE FROM {StagingTable};", RollupRecomputeSqlBuilder.BuildDeleteAllStaged(StagingTable));
    }

    [Fact]
    public void BuildCountSuperseded_HasThePredicateOfTheSweep()
    {
        var count = RollupRecomputeSqlBuilder.BuildCountSuperseded(LiveTable, SliceFrom, SliceTo, 7, "mp-42");
        var sweep = RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(LiveTable, SliceFrom, SliceTo, 7, "mp-42");

        Assert.Equal(
            $"SELECT COUNT(*) FROM {LiveTable} WHERE \"window_start\" >= {SliceFromMs} AND \"window_start\" < {SliceToMs} " +
            "AND \"generation\" != 7 AND \"rtid\" = 'mp-42';",
            count);
        // Same rows: the count is what sizes the sweep slice.
        Assert.Equal(count.Replace("SELECT COUNT(*) FROM", "DELETE FROM"), sweep);
    }

    [Fact]
    public void BuildCountSuperseded_WithoutScope_HasNoRtIdPredicate()
    {
        var sql = RollupRecomputeSqlBuilder.BuildCountSuperseded(LiveTable, SliceFrom, SliceTo, 7);

        Assert.EndsWith("AND \"generation\" != 7;", sql);
        Assert.DoesNotContain("rtid", sql);
    }

    [Fact]
    public void BuildCountGeneration_And_BuildDeleteGenerationSlice_SelectExactlyThatGeneration()
    {
        var count = RollupRecomputeSqlBuilder.BuildCountGeneration(LiveTable, SliceFrom, SliceTo, 7, "mp-42");
        var delete = RollupRecomputeSqlBuilder.BuildDeleteGenerationSlice(LiveTable, SliceFrom, SliceTo, 7, "mp-42");

        Assert.Equal(
            $"SELECT COUNT(*) FROM {LiveTable} WHERE \"window_start\" >= {SliceFromMs} AND \"window_start\" < {SliceToMs} " +
            "AND \"generation\" = 7 AND \"rtid\" = 'mp-42';",
            count);
        Assert.Equal(count.Replace("SELECT COUNT(*) FROM", "DELETE FROM"), delete);
        Assert.DoesNotContain("rtid",
            RollupRecomputeSqlBuilder.BuildDeleteGenerationSlice(LiveTable, SliceFrom, SliceTo, 7));
    }

    [Fact]
    public void BuildCountBelowGeneration_And_BuildDeleteBelowGenerationSlice_SelectStrictlyLowerGenerations()
    {
        var count = RollupRecomputeSqlBuilder.BuildCountBelowGeneration(LiveTable, SliceFrom, SliceTo, 7, "mp-42");
        var delete = RollupRecomputeSqlBuilder.BuildDeleteBelowGenerationSlice(LiveTable, SliceFrom, SliceTo, 7, "mp-42");

        // Strictly below, never "different from": a newer overlapping recompute's rows must stay.
        Assert.Equal(
            $"SELECT COUNT(*) FROM {LiveTable} WHERE \"window_start\" >= {SliceFromMs} AND \"window_start\" < {SliceToMs} " +
            "AND \"generation\" < 7 AND \"rtid\" = 'mp-42';",
            count);
        Assert.Equal(count.Replace("SELECT COUNT(*) FROM", "DELETE FROM"), delete);
        Assert.DoesNotContain("rtid",
            RollupRecomputeSqlBuilder.BuildDeleteBelowGenerationSlice(LiveTable, SliceFrom, SliceTo, 7));
    }

    [Fact]
    public void SliceBuilders_EscapeTheScopeLiteral()
    {
        var sql = RollupRecomputeSqlBuilder.BuildDeleteBelowGenerationSlice(LiveTable, SliceFrom, SliceTo, 7, "a'b");

        Assert.Contains("\"rtid\" = 'a''b'", sql);
    }

    [Fact]
    public void BuildSweepSupersededGenerations_DeletesOtherGenerationsInRange_NumericBounds()
    {
        var from = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 11, 13, 0, 0, DateTimeKind.Utc);
        var fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to).ToUnixTimeMilliseconds();

        var sql = RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(LiveTable, from, to, 7);

        Assert.Equal(
            $"DELETE FROM {LiveTable} WHERE \"window_start\" >= {fromMs} AND \"window_start\" < {toMs} " +
            $"AND \"generation\" != 7;",
            sql);
        // Window bounds are numeric epoch-ms, never quoted timestamp literals.
        Assert.DoesNotContain("'", sql);
    }

    [Fact]
    public void BuildSweepSupersededGenerations_WithRtIdScope_AddsRtIdPredicate()
    {
        var from = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 11, 13, 0, 0, DateTimeKind.Utc);

        var sql = RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(LiveTable, from, to, 3, "mp-42");

        Assert.Contains("AND \"generation\" != 3", sql);
        Assert.Contains("AND \"rtid\" = 'mp-42'", sql);
    }

    [Fact]
    public void BuildSweepSupersededGenerations_TreatsUnspecifiedKindAsUtc()
    {
        var unspecified = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(LiveTable, utc, utc.AddHours(1), 1),
            RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(LiveTable, unspecified, unspecified.AddHours(1), 1));
    }

    [Fact]
    public void BuildDeleteRecomputedRowsFrom_DeletesOnlyNonZeroGenerationsAtOrAfterBoundary()
    {
        var from = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
        var fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds();

        var sql = RollupRecomputeSqlBuilder.BuildDeleteRecomputedRowsFrom(LiveTable, from);

        Assert.Equal(
            $"DELETE FROM {LiveTable} WHERE \"window_start\" >= {fromMs} AND \"generation\" != 0;",
            sql);
        // Boundary is numeric epoch-ms, never a quoted timestamp literal.
        Assert.DoesNotContain("'", sql);
    }

    [Fact]
    public void BuildDeleteRecomputedRowsFrom_TreatsUnspecifiedKindAsUtc()
    {
        var unspecified = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            RollupRecomputeSqlBuilder.BuildDeleteRecomputedRowsFrom(LiveTable, utc),
            RollupRecomputeSqlBuilder.BuildDeleteRecomputedRowsFrom(LiveTable, unspecified));
    }
}
