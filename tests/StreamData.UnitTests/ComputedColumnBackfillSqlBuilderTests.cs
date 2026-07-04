using System;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Pure SQL for the active-archive computed-column backfill (AB#4189 Phase 7, §8). Row-keyed so the
/// same builder serves raw (<c>timestamp, rtid, cktypeid</c>) and time-range
/// (<c>window_start, window_end, rtid, cktypeid</c>) archives.
/// </summary>
public class ComputedColumnBackfillSqlBuilderTests
{
    private static readonly DateTime Ts = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    private const string Table = "\"energy\".\"archive_x\"";

    [Fact]
    public void BuildSelect_Raw_ProjectsKeyThenValueColumns_OrderedByKey()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildSelect(
            Table,
            keyColumns: new[] { "timestamp", "rtid", "cktypeid" },
            valueColumns: new[] { "activepower", "apparentpower" });

        Assert.Equal(
            "SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"activepower\", \"apparentpower\" " +
            "FROM \"energy\".\"archive_x\" ORDER BY \"timestamp\", \"rtid\", \"cktypeid\";",
            sql);
    }

    [Fact]
    public void BuildSelect_AppendsLimit_WhenPaging()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildSelect(
            Table,
            keyColumns: new[] { "timestamp" },
            valueColumns: new[] { "v" },
            limit: 500);

        Assert.Equal(
            "SELECT \"timestamp\", \"v\" FROM \"energy\".\"archive_x\" ORDER BY \"timestamp\" LIMIT 500;",
            sql);
    }

    [Fact]
    public void BuildSelect_KeysetCursor_EmitsTupleGreaterThanPredicate()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildSelect(
            Table,
            keyColumns: new[] { "timestamp", "rtid", "cktypeid" },
            valueColumns: new[] { "v" },
            limit: 1000,
            cursor: new (string, object?)[]
            {
                ("timestamp", Ts),
                ("rtid", "rt-1"),
                ("cktypeid", "EnergyMeter"),
            });

        const string ts = "'2026-06-28T12:00:00.000Z'::timestamp with time zone";
        Assert.Equal(
            "SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"v\" FROM \"energy\".\"archive_x\" WHERE " +
            $"((\"timestamp\" > {ts}) " +
            $"OR (\"timestamp\" = {ts} AND \"rtid\" > 'rt-1') " +
            $"OR (\"timestamp\" = {ts} AND \"rtid\" = 'rt-1' AND \"cktypeid\" > 'EnergyMeter')) " +
            "ORDER BY \"timestamp\", \"rtid\", \"cktypeid\" LIMIT 1000;",
            sql);
    }

    [Fact]
    public void BuildSelect_TimeRange_KeysOnWindowColumns()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildSelect(
            Table,
            keyColumns: new[] { "window_start", "window_end", "rtid", "cktypeid" },
            valueColumns: new[] { "energy" });

        Assert.Equal(
            "SELECT \"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"energy\" " +
            "FROM \"energy\".\"archive_x\" ORDER BY \"window_start\", \"window_end\", \"rtid\", \"cktypeid\";",
            sql);
    }

    [Fact]
    public void BuildBulkUpdate_TimeRange_ParameterisedByTargetAndKeys()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildBulkUpdate(
            Table,
            targetColumn: "powerfactor",
            keyColumns: new[] { "window_start", "window_end", "rtid", "cktypeid" });

        Assert.Equal(
            "UPDATE \"energy\".\"archive_x\" SET \"powerfactor\" = $1 WHERE " +
            "\"window_start\" = $2 AND \"window_end\" = $3 AND \"rtid\" = $4 AND \"cktypeid\" = $5;",
            sql);
    }

    [Fact]
    public void BuildUpdate_Raw_SetsComputedCell_AddressedByRowKey()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildUpdate(
            Table,
            assignments: new (string, object?)[] { ("powerfactor", 0.95d) },
            keyPredicates: new (string, object?)[]
            {
                ("timestamp", Ts),
                ("rtid", "rt-1"),
                ("cktypeid", "EnergyMeter"),
            });

        Assert.Equal(
            "UPDATE \"energy\".\"archive_x\" SET \"powerfactor\" = 0.95 " +
            "WHERE \"timestamp\" = '2026-06-28T12:00:00.000Z'::timestamp with time zone " +
            "AND \"rtid\" = 'rt-1' AND \"cktypeid\" = 'EnergyMeter';",
            sql);
    }

    [Fact]
    public void BuildUpdate_NullComputedValue_EmitsNull()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildUpdate(
            Table,
            assignments: new (string, object?)[] { ("powerfactor", null) },
            keyPredicates: new (string, object?)[] { ("rtid", "rt-1") });

        Assert.Equal(
            "UPDATE \"energy\".\"archive_x\" SET \"powerfactor\" = NULL WHERE \"rtid\" = 'rt-1';",
            sql);
    }

    [Fact]
    public void BuildUpdate_MultipleAssignments_BooleanAndDateTimeLiterals()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildUpdate(
            Table,
            assignments: new (string, object?)[] { ("ok", true), ("at", Ts) },
            keyPredicates: new (string, object?)[] { ("rtid", "rt-1") });

        Assert.Equal(
            "UPDATE \"energy\".\"archive_x\" SET \"ok\" = TRUE, " +
            "\"at\" = '2026-06-28T12:00:00.000Z'::timestamp with time zone WHERE \"rtid\" = 'rt-1';",
            sql);
    }

    [Fact]
    public void BuildUpdate_EscapesSingleQuotesInKeyValues()
    {
        var sql = ComputedColumnBackfillSqlBuilder.BuildUpdate(
            Table,
            assignments: new (string, object?)[] { ("v", 1L) },
            keyPredicates: new (string, object?)[] { ("rtid", "o'brien") });

        Assert.Equal(
            "UPDATE \"energy\".\"archive_x\" SET \"v\" = 1 WHERE \"rtid\" = 'o''brien';",
            sql);
    }
}
