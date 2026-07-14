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
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        // AVG → two stored columns: voltage_avg_sum (SUM) + voltage_avg_count (COUNT).
        Assert.Contains("\"voltage_avg_sum\"", sql);
        Assert.Contains("\"voltage_avg_count\"", sql);
        Assert.Contains("SUM(\"voltage\") AS \"voltage_avg_sum\"", sql);
        Assert.Contains("COUNT(\"voltage\") AS \"voltage_avg_count\"", sql);
        // No bare AVG column — chained rollups need the components separately.
        Assert.DoesNotContain(" AVG(", sql);
    }

    [Fact]
    public void Build_WithRtIdScope_RestrictsAggregationToThatEntity_BeforeGroupBy()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false, rtIdScope: "mp-42");

        Assert.Contains("AND \"rtid\" = 'mp-42'", sql);
        // The rtId filter is part of the WHERE (before GROUP BY), not after.
        Assert.True(
            sql.IndexOf("AND \"rtid\" = 'mp-42'", StringComparison.Ordinal)
            < sql.IndexOf("GROUP BY", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithoutRtIdScope_EmitsNoRtIdFilter()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.DoesNotContain("AND \"rtid\" =", sql);
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
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains($"\"{columnName}\"", sql);
        Assert.Contains($"{sqlFn}(\"voltage\") AS \"{columnName}\"", sql);
    }

    [Fact]
    public void Build_ExplicitTargetColumnName_OverridesDefault()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Max, "vmax") };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains("MAX(\"voltage\") AS \"vmax\"", sql);
        Assert.DoesNotContain("\"voltage_max\"", sql);
    }

    [Fact]
    public void Build_DottedSourcePath_CollapsesToLowercaseColumn()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("sensor.reading.value", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

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
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains("SUM(\"voltage\") AS \"voltage_avg_sum\"", sql);
        Assert.Contains("COUNT(\"voltage\") AS \"voltage_avg_count\"", sql);
        Assert.Contains("MIN(\"current\") AS \"current_min\"", sql);
        Assert.Contains("MAX(\"current\") AS \"current_max\"", sql);
    }

    [Fact]
    public void Build_RawSource_StatementShape_HasInsertSelectGroupByOnConflict()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        // Phase 7: target table always uses windowed (window_start, window_end) shape.
        // Phase 8: raw source ⇒ WHERE on single `timestamp` column, no was_updated propagation.
        Assert.StartsWith("INSERT INTO \"acmecorp\".\"archive_target\"", sql);
        Assert.Contains("\"window_start\"", sql);
        Assert.Contains("\"window_end\"", sql);
        Assert.Contains("FROM \"acmecorp\".\"archive_source\"", sql);
        Assert.Contains("WHERE \"timestamp\" >= '", sql);
        Assert.Contains("AND \"timestamp\" < '", sql);
        Assert.Contains("GROUP BY \"rtid\"", sql);
        // Phase 6: generation is part of the rollup conflict key and is written as the steady-state 0.
        Assert.Contains("ON CONFLICT (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"generation\") DO UPDATE SET", sql);
        Assert.Contains("0 AS \"generation\"", sql);
        Assert.Contains("\"voltage_sum\" = EXCLUDED.\"voltage_sum\"", sql);
        Assert.Contains("\"was_updated\" = TRUE", sql);
        // No source.was_updated propagation for a raw source — column does not exist there.
        Assert.DoesNotContain("MAX(\"was_updated\")", sql);
        Assert.EndsWith(";", sql);
    }

    [Fact]
    public void Build_WindowedSource_UsesFullyContainedWindowPredicate()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true);

        // Phase 8 / concept §7: fully-contained predicate over (window_start, window_end).
        Assert.Contains("WHERE \"window_start\" >= '", sql);
        Assert.Contains("AND \"window_end\" <= '", sql);
        // Single-`timestamp` predicate must not appear when source is windowed.
        Assert.DoesNotContain("WHERE \"timestamp\"", sql);
        Assert.DoesNotContain("AND \"timestamp\" <", sql);
    }

    [Fact]
    public void Build_WindowedSource_PropagatesWasUpdatedFromSource()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true);

        // Insert side: was_updated is in the target column list and SELECT MAX(was_updated).
        Assert.Contains("\"was_updated\"", sql);
        Assert.Contains("MAX(\"was_updated\") AS \"was_updated\"", sql);
        // Conflict side still flips to TRUE — re-aggregation is itself a correction.
        Assert.Contains("\"was_updated\" = TRUE", sql);
    }

    [Fact]
    public void Build_EmitsBucketBoundariesAsWindowedTimestampLiterals()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("v", CkRollupFunction.Sum, null) };
        var bucketStart = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
        var bucketEnd = new DateTime(2026, 5, 11, 14, 5, 0, DateTimeKind.Utc);

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, bucketStart, bucketEnd,
            sourceUsesWindowedStorage: false);

        // Phase 7: bucketStart → window_start, bucketEnd → window_end.
        Assert.Contains("'2026-05-11T14:00:00.0000000Z'::timestamp AS \"window_start\"", sql);
        Assert.Contains("'2026-05-11T14:05:00.0000000Z'::timestamp AS \"window_end\"", sql);
    }

    [Fact]
    public void Build_EscapesSingleQuoteInRollupCkTypeId()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("v", CkRollupFunction.Sum, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, "weird'name", aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

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
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false));
    }

    [Fact]
    public void Build_EmptyAggregations_Throws()
    {
        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId,
            Array.Empty<CkRollupAggregationSpec>(), BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false));
    }

    [Fact]
    public void Build_BucketEndNotAfterStart_Throws()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("v", CkRollupFunction.Sum, null) };
        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketEnd, BucketStart,
            sourceUsesWindowedStorage: false));
    }

    // ---- TimeWeightedAvg / LOCF carry (AB#4336) ----

    [Fact]
    public void Build_TimeWeightedAvg_RawSource_EmitsLocfCarryStatement()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("dimmingLevel", CkRollupFunction.TimeWeightedAvg, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        // TWA → two stored columns: integral (value*ms) + covered duration (ms).
        Assert.Contains("\"dimminglevel_twavg_integral\"", sql);
        Assert.Contains("\"dimminglevel_twavg_duration\"", sql);
        Assert.Contains(
            "SUM(CASE WHEN \"dimminglevel\" IS NOT NULL THEN \"dimminglevel\" * \"dt_ms\" END) AS \"dimminglevel_twavg_integral\"",
            sql);
        Assert.Contains(
            "SUM(CASE WHEN \"dimminglevel\" IS NOT NULL THEN \"dt_ms\" END) AS \"dimminglevel_twavg_duration\"",
            sql);
        // Carry-in: latest observation before the bucket, surfaced as a virtual event at B_start.
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"timestamp\" DESC) AS \"rn\"", sql);
        Assert.Contains("TRUE AS \"is_carry\"", sql);
        // Interval weighting via LEAD, carry ordered first on ties, capped at the bucket end.
        Assert.Contains("LEAD(\"ts\") OVER (PARTITION BY \"rtid\" ORDER BY \"ts\", \"is_carry\" DESC)", sql);
        // Default lookback bound: 35 days before the bucket start (2026-05-11 → 2026-04-06).
        Assert.Contains("'2026-04-06T14:00:00.0000000Z'::timestamp", sql);
        // Still an idempotent upsert on the standard conflict key.
        Assert.Contains("ON CONFLICT (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"generation\")", sql);
    }

    [Fact]
    public void Build_TimeWeightedAvg_CustomCarryLookback_BoundsTheCarryScan()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("dimmingLevel", CkRollupFunction.TimeWeightedAvg, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false, carryLookback: TimeSpan.FromDays(7));

        Assert.Contains("'2026-05-04T14:00:00.0000000Z'::timestamp", sql);
    }

    [Fact]
    public void Build_TimeWeightedAvg_MixedWithPlainAggregation_GuardsPlainAggregatesAgainstCarryRow()
    {
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Max, null),
            new CkRollupAggregationSpec("dimmingLevel", CkRollupFunction.TimeWeightedAvg, null),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        // The plain MAX must not see the carry-in virtual row — it lies outside the bucket.
        Assert.Contains("MAX(CASE WHEN NOT \"is_carry\" THEN \"voltage\" END) AS \"voltage_max\"", sql);
    }

    [Fact]
    public void Build_TimeWeightedAvg_WindowedSource_WeightsByWindowLength_NoCarry()
    {
        // Over a windowed source (TimeRangeArchive) the weight is the row's own window length —
        // the windows ARE the coverage; no LOCF machinery.
        var aggregations = new[] { new CkRollupAggregationSpec("dimmingLevel", CkRollupFunction.TimeWeightedAvg, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true);

        Assert.Contains(
            "SUM(CASE WHEN \"dimminglevel\" IS NOT NULL THEN \"dimminglevel\" * (\"window_end\"::bigint - \"window_start\"::bigint) END)",
            sql);
        Assert.DoesNotContain("is_carry", sql);
        Assert.DoesNotContain("LEAD(", sql);
    }

    [Fact]
    public void Build_TimeWeightedAvg_RtIdScope_AppliesToCarryAndBucketScans()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("dimmingLevel", CkRollupFunction.TimeWeightedAvg, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false, rtIdScope: "mp-42");

        // Both the carry-in scan and the in-bucket scan must be scoped.
        var occurrences = sql.Split("AND \"rtid\" = 'mp-42'").Length - 1;
        Assert.Equal(2, occurrences);
    }

    // ---- StateDuration (AB#4336) ----

    [Fact]
    public void Build_StateDuration_RawSource_EmitsComparisonGuardedDurationInLocfShape()
    {
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("isOn", CkRollupFunction.StateDuration, null, "true"),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains("SUM(CASE WHEN \"ison\" = TRUE THEN \"dt_ms\" END) AS \"ison_stateduration\"", sql);
        Assert.Contains("TRUE AS \"is_carry\"", sql); // LOCF carry applies to StateDuration too
    }

    [Fact]
    public void Build_StateDuration_NumericComparison_RendersNumericLiteral()
    {
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("machineState", CkRollupFunction.StateDuration, null, "2"),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains("SUM(CASE WHEN \"machinestate\" = 2 THEN \"dt_ms\" END)", sql);
    }

    [Fact]
    public void Build_StateDuration_WindowedSource_WeightsByWindowLength()
    {
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("isOn", CkRollupFunction.StateDuration, null, "true"),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true);

        Assert.Contains(
            "SUM(CASE WHEN \"ison\" = TRUE THEN (\"window_end\"::bigint - \"window_start\"::bigint) END)",
            sql);
        Assert.DoesNotContain("is_carry", sql);
    }

    // ---- First / Last (AB#4188) ----

    [Fact]
    public void Build_First_RawSource_PicksValueOfEarliestRowViaRowNumber()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.First, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        // CrateDB has no arg_min — the value at the earliest timestamp is picked via a ROW_NUMBER
        // window in a wrapping sub-select, then MAX over the single rn = 1 row.
        Assert.Contains(
            "ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"timestamp\" ASC) AS \"_rn_first\"",
            sql);
        Assert.Contains("MAX(CASE WHEN \"_rn_first\" = 1 THEN \"voltage\" END) AS \"voltage_first\"", sql);
        Assert.Contains("FROM (", sql);
        Assert.Contains(") \"src\"", sql);
        // No invalid array-aggregate idiom.
        Assert.DoesNotContain("ARRAY[", sql);
    }

    [Fact]
    public void Build_Last_RawSource_PicksValueOfLatestRowViaRowNumber()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Last, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains(
            "ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"timestamp\" DESC) AS \"_rn_last\"",
            sql);
        Assert.Contains("MAX(CASE WHEN \"_rn_last\" = 1 THEN \"voltage\" END) AS \"voltage_last\"", sql);
    }

    [Fact]
    public void Build_First_WindowedSource_OrdersByWindowEnd()
    {
        // Cascade / time-range source: rank by the child window boundary, not a raw timestamp.
        var aggregations = new[] { new CkRollupAggregationSpec("voltage_first", CkRollupFunction.First, null) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true);

        Assert.Contains(
            "ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"window_end\" ASC) AS \"_rn_first\"",
            sql);
        Assert.Contains("MAX(CASE WHEN \"_rn_first\" = 1 THEN \"voltage_first\" END)", sql);
    }

    [Fact]
    public void Build_Last_MixedWithTimeWeighted_GuardsAgainstCarryRow()
    {
        // In the LOCF path (a TWA aggregation forces it) First/Last rank the in-bucket events, with
        // carry rows sorted last and excluded via AND NOT is_carry.
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Last, null),
            new CkRollupAggregationSpec("dimmingLevel", CkRollupFunction.TimeWeightedAvg, null),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Contains(
            "ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY (CASE WHEN \"is_carry\" THEN 1 ELSE 0 END) ASC, \"ts\" DESC) AS \"_rn_last\"",
            sql);
        Assert.Contains(
            "MAX(CASE WHEN \"_rn_last\" = 1 AND NOT \"is_carry\" THEN \"voltage\" END) AS \"voltage_last\"",
            sql);
        Assert.DoesNotContain("ARRAY[", sql);
    }

    // ---- One-pass proof (AB#4188 acceptance criterion) ----

    [Fact]
    public void Build_NAggregationsDifferentAttributes_SingleInsertSingleGroupBy()
    {
        // The core AB#4188 guarantee: N aggregations across different attributes materialise in a
        // single INSERT ... SELECT ... GROUP BY — one scan over the source window, not N.
        var aggregations = new[]
        {
            new CkRollupAggregationSpec("energy", CkRollupFunction.Sum, null),
            new CkRollupAggregationSpec("power", CkRollupFunction.Max, null),
            new CkRollupAggregationSpec("power", CkRollupFunction.Min, null),
            new CkRollupAggregationSpec("voltage", CkRollupFunction.First, null),
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Last, null),
            new CkRollupAggregationSpec("current", CkRollupFunction.Avg, null),
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false);

        Assert.Equal(1, CountOccurrences(sql, "INSERT INTO "));
        Assert.Equal(1, CountOccurrences(sql, "GROUP BY "));
        Assert.Equal(1, CountOccurrences(sql, "FROM \"acmecorp\".\"archive_source\""));
        // Every declared aggregate is present in the one statement.
        Assert.Contains("SUM(\"energy\") AS \"energy_sum\"", sql);
        Assert.Contains("MAX(\"power\") AS \"power_max\"", sql);
        Assert.Contains("MIN(\"power\") AS \"power_min\"", sql);
        Assert.Contains("AS \"voltage_first\"", sql);
        Assert.Contains("AS \"voltage_last\"", sql);
        Assert.Contains("SUM(\"current\") AS \"current_avg_sum\"", sql);
        Assert.Contains("COUNT(\"current\") AS \"current_avg_count\"", sql);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
