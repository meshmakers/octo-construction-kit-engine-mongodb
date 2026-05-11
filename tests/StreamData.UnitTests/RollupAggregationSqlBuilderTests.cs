using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RollupAggregationSqlBuilderTests
{
    private const string SourceTable = "\"acmecorp\".\"archive_source\"";
    private const string TargetTable = "\"acmecorp\".\"archive_target\"";
    private const string RollupCkTypeId = "System.StreamData/CkRollupArchive-1";

    private static readonly DateTime BucketStart = new(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BucketEnd = new(2026, 5, 11, 14, 1, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_AvgFunction_GeneratesSumAndCountColumns()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd);

        // AVG → two stored columns: voltage_avg_sum (SUM) + voltage_avg_count (COUNT).
        Assert.Contains("\"voltage_avg_sum\"", sql);
        Assert.Contains("\"voltage_avg_count\"", sql);
        Assert.Contains("SUM(\"voltage\") AS \"voltage_avg_sum\"", sql);
        Assert.Contains("COUNT(\"voltage\") AS \"voltage_avg_count\"", sql);
        // No bare AVG column — chained rollups need the components separately.
        Assert.DoesNotContain(" AVG(", sql);
    }

    [Theory]
    [InlineData(CkRollupFunction.Min, "MIN", "voltage_min")]
    [InlineData(CkRollupFunction.Max, "MAX", "voltage_max")]
    [InlineData(CkRollupFunction.Sum, "SUM", "voltage_sum")]
    [InlineData(CkRollupFunction.Count, "COUNT", "voltage_count")]
    public void Build_SimpleFunction_GeneratesSingleColumn(CkRollupFunction fn, string sqlFn, string columnName)
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", fn, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd);

        Assert.Contains($"\"{columnName}\"", sql);
        Assert.Contains($"{sqlFn}(\"voltage\") AS \"{columnName}\"", sql);
    }

    [Fact]
    public void Build_ExplicitTargetColumnName_OverridesDefault()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Max, "vmax") };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd);

        Assert.Contains("MAX(\"voltage\") AS \"vmax\"", sql);
        Assert.DoesNotContain("\"voltage_max\"", sql);
    }

    [Fact]
    public void Build_DottedSourcePath_CollapsesToLowercaseColumn()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("sensor.reading.value", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd);

        Assert.Contains("SUM(\"sensorreadingvalue\")", sql);
        Assert.Contains("AS \"sensorreadingvalue_sum\"", sql);
    }

    [Fact]
    public void Build_MultipleAggregations_AllAppear()
    {
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, null),
            new CkRollupAggregationSpec("current", CkRollupFunction.Min, null),
            new CkRollupAggregationSpec("current", CkRollupFunction.Max, null),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd);

        Assert.Contains("SUM(\"voltage\") AS \"voltage_avg_sum\"", sql);
        Assert.Contains("COUNT(\"voltage\") AS \"voltage_avg_count\"", sql);
        Assert.Contains("MIN(\"current\") AS \"current_min\"", sql);
        Assert.Contains("MAX(\"current\") AS \"current_max\"", sql);
    }

    [Fact]
    public void Build_StatementShape_HasInsertSelectGroupByOnConflict()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd);

        Assert.StartsWith("INSERT INTO \"acmecorp\".\"archive_target\"", sql);
        Assert.Contains("FROM \"acmecorp\".\"archive_source\"", sql);
        Assert.Contains("WHERE \"timestamp\" >= '", sql);
        Assert.Contains("AND \"timestamp\" < '", sql);
        Assert.Contains("GROUP BY \"rtid\"", sql);
        Assert.Contains("ON CONFLICT (\"timestamp\", \"rtid\", \"cktypeid\") DO UPDATE SET", sql);
        Assert.Contains("\"voltage_sum\" = EXCLUDED.\"voltage_sum\"", sql);
        Assert.EndsWith(";", sql);
    }

    [Fact]
    public void Build_EmitsBucketEndAsTimestampLiteral()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("v", CkRollupFunction.Sum, null) };
        var bucketStart = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
        var bucketEnd = new DateTime(2026, 5, 11, 14, 5, 0, DateTimeKind.Utc);

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, bucketStart, bucketEnd);

        Assert.Contains("'2026-05-11T14:05:00.0000000Z'::timestamp AS \"timestamp\"", sql);
        Assert.Contains("'2026-05-11T14:00:00.0000000Z'::timestamp", sql);
    }

    [Fact]
    public void Build_EscapesSingleQuoteInRollupCkTypeId()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("v", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, "weird'name", aggregations, BucketStart, BucketEnd);

        // Single quote in the CK type id is doubled per SQL escaping rules.
        Assert.Contains("'weird''name'", sql);
    }

    [Fact]
    public void Build_DuplicateTargetColumns_Throws()
    {
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Max, "v"),
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Min, "v"), // collision
        };

        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd));
    }

    [Fact]
    public void Build_EmptyAggregations_Throws()
    {
        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId,
            Array.Empty<CkRollupAggregationSpec>(), BucketStart, BucketEnd));
    }

    [Fact]
    public void Build_BucketEndNotAfterStart_Throws()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("v", CkRollupFunction.Sum, null) };
        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketEnd, BucketStart));
    }
}
