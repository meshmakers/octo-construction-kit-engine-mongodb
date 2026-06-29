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
    public void BuildRangeDelete_BoundsWindowStartByEpochMillis()
    {
        var from = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 11, 13, 0, 0, DateTimeKind.Utc);
        var fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to).ToUnixTimeMilliseconds();

        var sql = RollupRecomputeSqlBuilder.BuildRangeDelete(LiveTable, from, to);

        Assert.Equal(
            $"DELETE FROM {LiveTable} WHERE \"window_start\" >= {fromMs} AND \"window_start\" < {toMs};",
            sql);
        // Numeric bounds — never quoted as a timestamp string literal.
        Assert.DoesNotContain("'", sql);
    }

    [Fact]
    public void BuildRangeDelete_TreatsUnspecifiedKindAsUtc()
    {
        var unspecified = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            RollupRecomputeSqlBuilder.BuildRangeDelete(LiveTable, utc, utc.AddHours(1)),
            RollupRecomputeSqlBuilder.BuildRangeDelete(LiveTable, unspecified, unspecified.AddHours(1)));
    }

    [Fact]
    public void BuildInsertFromStaging_CopiesExplicitQuotedColumnsBothSides()
    {
        var columns = new List<string> { "window_start", "window_end", "voltage_avg_sum" };

        var sql = RollupRecomputeSqlBuilder.BuildInsertFromStaging(LiveTable, StagingTable, columns);

        Assert.Equal(
            $"INSERT INTO {LiveTable} (\"window_start\", \"window_end\", \"voltage_avg_sum\") " +
            $"SELECT \"window_start\", \"window_end\", \"voltage_avg_sum\" FROM {StagingTable};",
            sql);
    }

    [Fact]
    public void BuildInsertFromStaging_EmptyColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RollupRecomputeSqlBuilder.BuildInsertFromStaging(LiveTable, StagingTable, Array.Empty<string>()));
    }
}
