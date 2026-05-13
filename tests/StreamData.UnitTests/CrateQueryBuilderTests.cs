using System.Globalization;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// All assertions target the per-archive table and camelCase column names introduced by T17 — the
// legacy "tenant"."streamData" table is gone, the `data` OBJECT(DYNAMIC) blob with it. Column
// names quoted with embedded camelCase, identifiers like `rtId`/`timestamp`/`ckTypeId` come
// directly from `Constants`.
public class CrateQueryBuilderTests
{
    private const string Table = "\"meshtest\".\"archive_a1\"";

    [Fact]
    public void SingleVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);

        var compiler = new CrateQueryCompiler();

        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal($"SELECT \"voltage\" FROM {Table}", query);
    }

    [Fact]
    public void IncludeDefaultVariables_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal($"SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"rtwellknownname\", \"rtcreationdatetime\", \"rtchangeddatetime\" FROM {Table}", query);
    }

    [Fact]
    public void IncludeDefaultVariablesAndSingleVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("voltage", null, null);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal($"SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"rtwellknownname\", \"rtcreationdatetime\", \"rtchangeddatetime\", \"voltage\" FROM {Table}", query);
    }

    [Fact]
    public void IncludeSingleVariableAndTimeFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            $"SELECT \"voltage\" FROM {Table} WHERE \"timestamp\" >= '2022-01-01 00:00:00.000Z' AND \"timestamp\" <= '2022-12-31 23:59:59.999Z'",
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAlias_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", "v", null);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal($"SELECT \"voltage\" AS \"v\" FROM {Table}", query);
    }

    [Fact]
    public void UseWindowedTimeAxis_AliasesWindowEndAsTimestampInSelect()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.UseWindowedTimeAxis();
        queryBuilder.IncludeDefaultVariables();

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        // Phase-A read-compatibility layer: window_end surfaces as "timestamp" to keep downstream
        // consumers archive-flavor-agnostic, with window_start exposed as a separate column for
        // callers that want the full half-open interval.
        Assert.Contains("\"window_end\" AS \"timestamp\"", query);
        Assert.Contains("\"window_start\"", query);
    }

    [Fact]
    public void UseWindowedTimeAxis_TimeFilterUsesWindowEndColumn()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.UseWindowedTimeAxis();
        queryBuilder.AddVariable("voltage_avg_sum", null, null);

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        // WHERE clause targets the physical column that actually exists on the windowed table.
        Assert.Contains("\"window_end\" >= '2022-01-01 00:00:00.000Z'", query);
        Assert.Contains("\"window_end\" <= '2022-12-31 23:59:59.999Z'", query);
        Assert.DoesNotContain("\"timestamp\" >=", query);
    }

    [Fact]
    public void IncludeSingleVariableWithAggregationFunctionAndDefaultVariables_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Avg, null);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            $"SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"rtwellknownname\", \"rtcreationdatetime\", \"rtchangeddatetime\", AVG(\"voltage\") AS \"Avg_voltage\" FROM {Table} GROUP BY \"timestamp\", \"rtid\", \"cktypeid\", \"rtwellknownname\", \"rtcreationdatetime\", \"rtchangeddatetime\"",
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAliasAndAggregationFunction_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Avg, "v");
        queryBuilder.AddVariable("timestamp", "t", null);
        queryBuilder.OrderBy("timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            $"SELECT \"timestamp\" AS \"t\", AVG(\"voltage\") AS \"v\" FROM {Table} GROUP BY \"t\" ORDER BY \"t\" ASC",
            query);
    }

    [Fact]
    public void IncludeCkTypeId_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.WithCkTypeIdFilter("Test/123");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            $"SELECT \"timestamp\", \"rtid\", \"cktypeid\", \"rtwellknownname\", \"rtcreationdatetime\", \"rtchangeddatetime\" FROM {Table} WHERE \"cktypeid\" = 'Test/123'",
            query);
    }

    [Fact]
    public void SingleFieldFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);
        queryBuilder.AddFieldFilter("voltage", StreamDataFieldFilterOperator.GreaterThan, "220");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            $"SELECT \"voltage\" FROM {Table} WHERE \"voltage\" > '220'",
            query);
    }

    [Fact]
    public void DefaultFieldFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("rtid", StreamDataFieldFilterOperator.Equals, "abc123");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"rtid\" = 'abc123'", query);
    }

    [Fact]
    public void LimitAndOffset_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);
        queryBuilder.WithLimit(10);
        queryBuilder.WithOffset(20);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.EndsWith("LIMIT 10 OFFSET 20", query);
    }

    [Fact]
    public void OrderByDefaultField_GeneratesCorrectSql()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.OrderBy("timestamp", SortOrderDto.Descending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("""ORDER BY "timestamp" DESC""", query);
    }

    [Fact]
    public void AddOrderByTiebreaker_AddsTimestampWhenNotPresent()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("received", "received", null);
        queryBuilder.OrderBy("received", SortOrderDto.Descending);
        queryBuilder.AddOrderByTiebreaker("timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("""ORDER BY "received" DESC, "timestamp" ASC""", query);
    }

    [Fact]
    public void DownsamplingWithAggregation_EmitsGenerateSeriesLeftJoin()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithDownsampling(10, from, to);
        queryBuilder.AddVariable("timestamp", "T", null);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Avg, "Avg_voltage");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("SELECT bins.ts AS \"T\"", sql);
        Assert.Contains("AVG(d.\"voltage\") AS \"Avg_voltage\"", sql);
        Assert.Contains("COUNT(d.\"timestamp\") AS \"__binCount\"", sql);
        Assert.Contains($"LEFT JOIN {Table} AS d ON DATE_BIN('360 seconds'::INTERVAL, d.\"timestamp\"", sql);
        Assert.Contains("d.\"cktypeid\" = 'Test/123'", sql);
    }

    [Fact]
    public void DownsamplingWithAggregation_AndFieldFilter_EmitsFilterInOnClause()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithDownsampling(6, from, to);
        queryBuilder.AddVariable("timestamp", "T", null);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Max, "Max_voltage");
        queryBuilder.AddFieldFilter("voltage", StreamDataFieldFilterOperator.GreaterThan, "0");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains($"LEFT JOIN {Table} AS d ON", sql);
        Assert.Contains("d.\"voltage\" > '0'", sql);
        Assert.DoesNotContain(" WHERE ", sql);
    }

    [Fact]
    public void CompileCountQuery_Basic_ReturnsValidCountQuery()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.WithCkTypeIdFilter("Test/123");

        var compiler = new CrateQueryCompiler();
        var countQuery = compiler.CompileCountQuery(queryBuilder);

        Assert.Equal(
            $"SELECT COUNT(*) FROM {Table} WHERE \"cktypeid\" = 'Test/123'",
            countQuery);
    }
}
