using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

/// <summary>
/// AB#7616: a row written before the origin was recorded stores NULL in row_origin and reads as automated on
/// every read path — projection, filter, grouping, downsampling and time-weighted aggregation.
/// </summary>
public class ArchiveRowOriginQuerySqlTests
{
    private const string Table = "\"meshtest\".\"archive_a1\"";
    private const string Origin = "COALESCE(\"row_origin\", 'automated')";

    [Fact]
    public void Projection_ReadsAMissingOriginAsAutomated_UnderTheColumnName()
    {
        var q = new CrateQueryBuilder(Table).IncludeDefaultVariables();
        q.AddVariable(Constants.RowOrigin, Constants.RowOrigin);
        q.AddVariable(Constants.ManualWrittenBy, Constants.ManualWrittenBy);

        var sql = new CrateQueryCompiler().CompileQuery(q);

        Assert.Contains($"{Origin} AS \"row_origin\"", sql);
        Assert.Contains("\"manual_written_by\"", sql);
        Assert.DoesNotContain("COALESCE(\"manual_written_by\"", sql);
    }

    [Theory]
    [InlineData(StreamDataFieldFilterOperator.Equals, "automated", Origin + " = 'automated'")]
    [InlineData(StreamDataFieldFilterOperator.NotEquals, "manual", Origin + " != 'manual'")]
    public void Filter_TreatsAMissingOriginAsAutomated(StreamDataFieldFilterOperator op, string value, string expected)
    {
        var q = new CrateQueryBuilder(Table).IncludeDefaultVariables();
        q.AddFieldFilter(Constants.RowOrigin, op, value);

        var sql = new CrateQueryCompiler().CompileQuery(q);

        Assert.Contains(expected, sql);
    }

    [Fact]
    public void Grouping_GroupsByTheSameExpressionItSelects()
    {
        var q = new CrateQueryBuilder(Table);
        q.AddVariable(Constants.RtId, Constants.RtId);
        q.AddVariable(Constants.RowOrigin, Constants.RowOrigin);
        q.AddAggregationVariable("value", AggregationFunctionDto.Sum, "sum_value");
        q.AddAggregationVariable("value", AggregationFunctionDto.Count, "count_value");

        var sql = new CrateQueryCompiler().CompileQuery(q);

        Assert.Contains($"{Origin} AS \"row_origin\"", sql);
        Assert.Contains($"GROUP BY \"rtid\", {Origin}", sql);
    }

    [Fact]
    public void Downsampling_QualifiesTheOriginWithTheTableAlias()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var q = new CrateQueryBuilder(Table).IncludeDefaultVariables();
        q.AddAggregationVariable("value", AggregationFunctionDto.Avg, "avg_value");
        q.AddFieldFilter(Constants.RowOrigin, StreamDataFieldFilterOperator.Equals, "manual");
        q.WithDownsampling(10, from, from.AddDays(1));
        q.WithDownsamplingGroupBy(Constants.RowOrigin);

        var sql = new CrateQueryCompiler().CompileQuery(q);

        Assert.Contains("COALESCE(d.\"row_origin\", 'automated') AS \"row_origin\"", sql);
        Assert.Contains("AND COALESCE(d.\"row_origin\", 'automated') = 'manual'", sql);
        Assert.DoesNotContain("d.COALESCE", sql);
    }

    [Fact]
    public void TimeWeightedAggregation_CarriesTheOriginAsAutomatedWhenMissing()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var sql = TimeWeightedQuerySqlBuilder.Build(
            Table, "Basic/Meter-1", from, from.AddDays(1),
            [new TimeWeightedQuerySqlBuilder.TwaColumn("value", "value_twavg")],
            Array.Empty<TimeWeightedQuerySqlBuilder.PlainColumn>(),
            [Constants.RowOrigin],
            rtIds: null,
            carryLookback: TimeSpan.FromDays(1));

        Assert.Contains($"{Origin} AS \"row_origin\"", sql);
        Assert.Contains("GROUP BY \"row_origin\"", sql);
    }

    [Fact]
    public void ThePlainColumnHelper_LeavesEveryOtherColumnAsAQuotedIdentifier()
    {
        Assert.Equal("\"value\"", ArchiveRowOriginColumns.ColumnRef("value"));
        Assert.Equal("d.\"value\"", ArchiveRowOriginColumns.ColumnRef("value", "d"));
        Assert.Equal("\"value\"", ArchiveRowOriginColumns.SelectRef("value"));
        Assert.Equal(ArchiveRowOrigin.Automated, ArchiveRowOriginColumns.FromStoredValue(null));
    }

    // AB#7727: the rows of a rollup aggregated before the column existed hold NULL, which means no manual values.
    [Fact]
    public void ManualCount_ReadsEmptyAsZero_InProjectionAndFilter_TheLatestTimeStaysEmpty()
    {
        var q = new CrateQueryBuilder(Table).IncludeDefaultVariables();
        q.AddVariable(Constants.ManualCount, Constants.ManualCount);
        q.AddVariable(Constants.LastManualAt, Constants.LastManualAt);
        q.AddFieldFilter(Constants.ManualCount, StreamDataFieldFilterOperator.GreaterThan, "0");

        var sql = new CrateQueryCompiler().CompileQuery(q);

        Assert.Contains("COALESCE(\"manual_count\", 0) AS \"manual_count\"", sql);
        Assert.Contains("COALESCE(\"manual_count\", 0) > ", sql);
        Assert.DoesNotContain("COALESCE(\"last_manual_at\"", sql);
    }
}
