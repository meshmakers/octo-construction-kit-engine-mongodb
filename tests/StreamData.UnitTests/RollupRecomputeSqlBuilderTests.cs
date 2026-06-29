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

    [Fact]
    public void BuildInsertFromStagingWithGeneration_StampsGenerationLiteralOnEveryRow()
    {
        var columns = new List<string> { "window_start", "window_end", "voltage_avg_sum" };

        var sql = RollupRecomputeSqlBuilder.BuildInsertFromStagingWithGeneration(LiveTable, StagingTable, columns, 7);

        Assert.Equal(
            $"INSERT INTO {LiveTable} (\"window_start\", \"window_end\", \"voltage_avg_sum\", \"generation\") " +
            $"SELECT \"window_start\", \"window_end\", \"voltage_avg_sum\", 7 FROM {StagingTable};",
            sql);
    }

    [Fact]
    public void BuildInsertFromStagingWithGeneration_EmptyColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RollupRecomputeSqlBuilder.BuildInsertFromStagingWithGeneration(LiveTable, StagingTable, Array.Empty<string>(), 1));
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
}
