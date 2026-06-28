using System.Globalization;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
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
    public void UseWindowedTimeAxis_TimeFilterUsesOverlapPredicate()
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

        // WHERE clause uses bucket-overlap semantics: window_start < to AND window_end > from.
        // Captures any bucket whose [start, end) interval intersects the requested range —
        // matches operator intent better than the previous single-column window_end IN [from, to]
        // which silently dropped buckets ending exactly at the range boundary (e.g. a Monthly
        // bucket ending 2026-02-01 is excluded from a Jan-1..Jan-31 filter).
        Assert.Contains("\"window_start\" < '2022-12-31 23:59:59.999Z'", query);
        Assert.Contains("\"window_end\" > '2022-01-01 00:00:00.000Z'", query);
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
    public void DownsamplingWithGroupBy_EmitsPerSeriesSelectGroupAndOrder()
    {
        // Multi-series downsampling (AB#4233): grouping by the source rtId keeps interleaved
        // series separated, and a MIN/MAX/AVG reducer set preserves the envelope (peaks) per bin.
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithDownsampling(10, from, to);
        queryBuilder.WithDownsamplingGroupBy("rtid");
        queryBuilder.AddVariable("timestamp", "T", null);
        queryBuilder.AddAggregationVariable("amountValue", AggregationFunctionDto.Avg, "amountValue_avg");
        queryBuilder.AddAggregationVariable("amountValue", AggregationFunctionDto.Min, "amountValue_min");
        queryBuilder.AddAggregationVariable("amountValue", AggregationFunctionDto.Max, "amountValue_max");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        // Series identity selected verbatim alongside the bin timestamp.
        Assert.Contains("SELECT bins.ts AS \"T\", d.\"rtid\" AS \"rtid\"", sql);
        // Envelope reducers: AVG centre line + MIN/MAX band, all per (bin, series).
        Assert.Contains("AVG(d.\"amountValue\") AS \"amountValue_avg\"", sql);
        Assert.Contains("MIN(d.\"amountValue\") AS \"amountValue_min\"", sql);
        Assert.Contains("MAX(d.\"amountValue\") AS \"amountValue_max\"", sql);
        // Group + order extended with the series column.
        Assert.Contains("GROUP BY bins.ts, d.\"rtid\"", sql);
        Assert.Contains("ORDER BY bins.ts ASC, d.\"rtid\" ASC", sql);
    }

    [Fact]
    public void DownsamplingWithoutGroupBy_KeepsSingleSeriesGrouping()
    {
        // Regression guard: group-by is opt-in — a plain downsampling query still groups only by
        // the time bin (no stray series column in SELECT / GROUP BY).
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithDownsampling(10, from, to);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Avg, "Avg_voltage");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("GROUP BY bins.ts ORDER BY bins.ts ASC", sql);
        Assert.DoesNotContain("d.\"rtid\"", sql);
    }

    [Fact]
    public void DownsamplingWithAggregation_WindowedTimeAxis_KeysOnWindowStartAndAddsContainmentCheck()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.UseWindowedTimeAxis();
        queryBuilder.WithDownsampling(6, from, to);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Max, "Max_voltage");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        // AB#4246: DATE_BIN keys on window_start — the bin that CONTAINS the window. Keying on
        // window_end assigned a boundary-aligned window to the next bin, so the fully-contained
        // predicate (window_start >= bins.ts) always failed and the bin read empty (all-null bug).
        Assert.Contains("DATE_BIN('600 seconds'::INTERVAL, d.\"window_start\"", sql);
        // __binCount still counts the time axis (window_end).
        Assert.Contains("COUNT(d.\"window_end\") AS \"__binCount\"", sql);
        Assert.DoesNotContain("DATE_BIN('600 seconds'::INTERVAL, d.\"window_end\"", sql);
        // Outer range filter uses bucket-overlap semantics — buckets whose body overlaps
        // [from, to] participate. Strict bin-containment (§7) is enforced separately below.
        Assert.Contains("d.\"window_start\" < '2024-01-01", sql);
        Assert.Contains("d.\"window_end\" > '2024-01-01", sql);
        // Concept-time-range §7 fully-contained predicate: source windows that straddle a bin
        // boundary are dropped, not pro-rated.
        Assert.Contains("d.\"window_start\" >= bins.ts", sql);
        Assert.Contains("d.\"window_end\" <= bins.ts +", sql);
        // No reference to the non-existent `timestamp` column on a windowed table.
        Assert.DoesNotContain("d.\"timestamp\"", sql);
    }

    [Fact]
    public void Downsampling_RawAggregationExpression_GetsTableAliasPrefixed()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.UseWindowedTimeAxis();
        queryBuilder.WithDownsampling(4, from, to);
        // Chain-aware AVG expression — every quoted column inside the call must get the d. alias
        // so the SQL references the joined source rows rather than the bins CTE.
        queryBuilder.AddRawAggregationExpression(
            "SUM(\"voltage_avg_sum\") / NULLIF(SUM(\"voltage_avg_count\"), 0)",
            "voltage_avg");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("SUM(d.\"voltage_avg_sum\") / NULLIF(SUM(d.\"voltage_avg_count\"), 0) AS \"voltage_avg\"", sql);
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

    // Regression for the rtIds source-scope bug: AddRtIdFilter called AddWhereIn with the literal
    // PascalCase "RtId", but the registered default variable (and CrateDB column) is
    // Constants.RtId == "rtid". The query-builder lookup is case-sensitive, so the scope threw
    // "WhereIn Variable not found: 'RtId'" and was silently broken on every SD query kind. These
    // tests lock the contract: the rtIds scope must compile to a WHERE "rtid" IN (...) clause.
    [Fact]
    public void AddWhereIn_RtIdScope_WithDefaultVariables_EmitsRtidInClause()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddWhereIn(Constants.RtId, ["6a0ee049425c29914c86a4f1", "6a0ee04a425c29914c86a54a"]);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"rtid\" IN ('6a0ee049425c29914c86a4f1', '6a0ee04a425c29914c86a54a')", query);
    }

    [Fact]
    public void AddWhereIn_RtIdScope_WindowedArchive_EmitsRtidInClause()
    {
        // TimeRangeArchives (e.g. the energy-measurements archive) use windowed storage, but the
        // default-variable set still registers "rtid", so the rtIds scope must work there too.
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.UseWindowedTimeAxis();
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddWhereIn(Constants.RtId, ["abc123"]);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"rtid\" IN ('abc123')", query);
    }

    [Fact]
    public void AddWhereIn_CaseMismatchedName_Throws()
    {
        // Guards the exact defect: the lookup is case-sensitive, so the PascalCase literal "RtId"
        // does not match the registered "rtid" column and must throw rather than silently no-op.
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();

        Assert.Throws<QueryBuilderException>(() =>
            queryBuilder.AddWhereIn("RtId", ["abc123"]));
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

    // AB#4246: the bucket-count probe counts distinct source bins under the same source filters the
    // downsampling query uses, so the caller can clamp an over-large requested bucket count down to
    // the data's resolution (otherwise windowed archives null out every bin).
    [Fact]
    public void DownsamplingBucketCountQuery_Windowed_CountsDistinctWindowStartWithFilters()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.UseWindowedTimeAxis();
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithTimeFilter(from, to);
        queryBuilder.AddFieldFilter(Constants.RtId, StreamDataFieldFilterOperator.In, string.Empty,
            valueList: ["r1", "r2"]);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileDownsamplingBucketCountQuery(queryBuilder);

        // Distinct of the bin column (window_start for windowed archives), aliased "c".
        Assert.Contains($"SELECT COUNT(DISTINCT d.\"window_start\") AS \"c\" FROM {Table} AS d WHERE 1 = 1", sql);
        // Same source filters as the downsampling query: ckType, windowed range overlap, rtId scope.
        Assert.Contains("d.\"cktypeid\" = 'Test/123'", sql);
        Assert.Contains("d.\"window_start\" < '2024-01-01", sql);
        Assert.Contains("d.\"window_end\" > '2024-01-01", sql);
        Assert.Contains("d.\"rtid\" IN ('r1', 'r2')", sql);
        // It's a scalar count — no generate_series / join / group-by.
        Assert.DoesNotContain("generate_series", sql);
        Assert.DoesNotContain("LEFT JOIN", sql);
    }

    [Fact]
    public void DownsamplingBucketCountQuery_Raw_CountsDistinctTimestamp()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithTimeFilter(from, to);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileDownsamplingBucketCountQuery(queryBuilder);

        Assert.Contains($"SELECT COUNT(DISTINCT d.\"timestamp\") AS \"c\" FROM {Table} AS d WHERE 1 = 1", sql);
        Assert.Contains("d.\"timestamp\" >= '2024-01-01", sql);
        Assert.Contains("d.\"timestamp\" <= '2024-01-01", sql);
    }

    // AB#4246: the bin interval is rounded to the nearest second (was integer-truncated). For 1222
    // bins over a day, 86400/1222 = 70.7 -> 71s, not 70s.
    [Fact]
    public void Downsampling_IntervalRounding_RoundsToNearestSecondNotTruncate()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-02T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithDownsampling(1222, from, to);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Avg, "Avg_voltage");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("DATE_BIN('71 seconds'::INTERVAL", sql);
        Assert.DoesNotContain("'70 seconds'", sql);
    }

    [Fact]
    public void Downsampling_IntervalRounding_NeverBelowOneSecond()
    {
        // 30s span over 100 bins rounds to 0s; clamped to a 1s floor so the SQL stays valid.
        var from = DateTime.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T00:00:30Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.WithDownsampling(100, from, to);
        queryBuilder.AddAggregationVariable("voltage", AggregationFunctionDto.Avg, "Avg_voltage");

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("DATE_BIN('1 seconds'::INTERVAL", sql);
        Assert.DoesNotContain("'0 seconds'", sql);
    }
}
