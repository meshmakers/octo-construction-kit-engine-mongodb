using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Validates the chain-aware aggregation mapping for ad-hoc queries against a
/// <c>RollupArchive</c>. The chain table (concept-time-range §7) must round-trip every supported
/// (source-spec, target-function) pair without drifting from the materialised column naming the
/// orchestrator emits in <c>RollupAggregationColumns</c>.
/// </summary>
public class RollupQueryAggregationResolverTests
{
    [Fact]
    public void AvgSpec_TargetAvg_ResolvesToSumOverCount()
    {
        var specs = new[] { new CkRollupAggregationSpec("Temperature", CkRollupFunction.Avg, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "Temperature", AggregationFunctionDto.Avg);

        Assert.NotNull(result);
        Assert.Equal(
            "SUM(\"temperature_avg_sum\") / NULLIF(SUM(\"temperature_avg_count\"), 0)",
            result!.SqlExpression);
        Assert.Equal("temperature_avg", result.SqlAlias);
        Assert.Equal("Temperature", result.OutputColumnName);
    }

    [Fact]
    public void AvgSpec_TargetSum_PullsSumColumn()
    {
        var specs = new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Avg, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "Voltage", AggregationFunctionDto.Sum);

        Assert.NotNull(result);
        // Sum over an AVG-materialised rollup re-uses the bucket sums.
        Assert.Equal("SUM(\"voltage_avg_sum\")", result!.SqlExpression);
    }

    [Fact]
    public void AvgSpec_TargetCount_PullsCountColumn()
    {
        var specs = new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Avg, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "Voltage", AggregationFunctionDto.Count);

        Assert.NotNull(result);
        // Total observation count = sum of per-bucket counts.
        Assert.Equal("SUM(\"voltage_avg_count\")", result!.SqlExpression);
    }

    [Fact]
    public void AvgSpec_TargetMin_IsUnresolvable()
    {
        var specs = new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Avg, null) };

        // MIN information was discarded when the source was bucket-averaged — chain can't
        // reconstruct it. Surface as null so the caller can decline cleanly.
        Assert.Null(RollupQueryAggregationResolver.Resolve(specs, "Voltage", AggregationFunctionDto.Min));
        Assert.Null(RollupQueryAggregationResolver.Resolve(specs, "Voltage", AggregationFunctionDto.Max));
    }

    [Theory]
    [InlineData(CkRollupFunction.Min, AggregationFunctionDto.Min, "MIN(\"voltage_min\")")]
    [InlineData(CkRollupFunction.Max, AggregationFunctionDto.Max, "MAX(\"voltage_max\")")]
    [InlineData(CkRollupFunction.Sum, AggregationFunctionDto.Sum, "SUM(\"voltage_sum\")")]
    // Sum-of-counts: chained COUNT over a rollup that materialised COUNT.
    [InlineData(CkRollupFunction.Count, AggregationFunctionDto.Count, "SUM(\"voltage_count\")")]
    public void SingleColumnSpec_SameFunction_ResolvesToMatchingAggregate(
        CkRollupFunction sourceFn,
        AggregationFunctionDto targetFn,
        string expectedSql)
    {
        var specs = new[] { new CkRollupAggregationSpec("Voltage", sourceFn, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "Voltage", targetFn);

        Assert.NotNull(result);
        Assert.Equal(expectedSql, result!.SqlExpression);
    }

    [Theory]
    [InlineData(CkRollupFunction.Min, AggregationFunctionDto.Max)]   // wrong direction
    [InlineData(CkRollupFunction.Sum, AggregationFunctionDto.Avg)]   // can't derive mean from sum alone
    [InlineData(CkRollupFunction.Max, AggregationFunctionDto.Sum)]   // chain table doesn't define this
    [InlineData(CkRollupFunction.Count, AggregationFunctionDto.Sum)] // sum of counts vs sum of values are different
    public void IncompatibleFunctionPairs_ReturnNull(
        CkRollupFunction sourceFn,
        AggregationFunctionDto targetFn)
    {
        var specs = new[] { new CkRollupAggregationSpec("Voltage", sourceFn, null) };

        Assert.Null(RollupQueryAggregationResolver.Resolve(specs, "Voltage", targetFn));
    }

    [Fact]
    public void MismatchedAttributePath_ReturnsNull()
    {
        var specs = new[] { new CkRollupAggregationSpec("Voltage", CkRollupFunction.Avg, null) };

        // The rollup doesn't aggregate "Temperature" — no chain mapping exists.
        Assert.Null(RollupQueryAggregationResolver.Resolve(specs, "Temperature", AggregationFunctionDto.Avg));
    }

    [Fact]
    public void AttributePathLookup_IsCaseInsensitive()
    {
        var specs = new[] { new CkRollupAggregationSpec("Temperature", CkRollupFunction.Avg, null) };

        // CK YAML and GraphQL projections can carry different casings; the resolver normalises.
        Assert.NotNull(RollupQueryAggregationResolver.Resolve(specs, "temperature", AggregationFunctionDto.Avg));
        Assert.NotNull(RollupQueryAggregationResolver.Resolve(specs, "TEMPERATURE", AggregationFunctionDto.Avg));
    }

    [Fact]
    public void MultipleSpecsSameAttribute_FirstCompatibleWins()
    {
        // Rollup aggregates Voltage twice: as MIN and as AVG. A target AVG query should pick
        // the AVG spec (compatible), even though the MIN spec appears first in the list.
        var specs = new[]
        {
            new CkRollupAggregationSpec("Voltage", CkRollupFunction.Min, null),
            new CkRollupAggregationSpec("Voltage", CkRollupFunction.Avg, null),
        };

        var result = RollupQueryAggregationResolver.Resolve(specs, "Voltage", AggregationFunctionDto.Avg);

        Assert.NotNull(result);
        Assert.Contains("voltage_avg_sum", result!.SqlExpression);
        Assert.Contains("voltage_avg_count", result.SqlExpression);
    }

    [Fact]
    public void EmptySpecList_ReturnsNull()
    {
        Assert.Null(RollupQueryAggregationResolver.Resolve(
            Array.Empty<CkRollupAggregationSpec>(), "Voltage", AggregationFunctionDto.Avg));
    }

    // ---- TimeWeightedAvg (AB#4336) ----

    [Fact]
    public void TimeWeightedAvgSpec_TargetTimeWeightedAvg_ResolvesToIntegralOverDuration()
    {
        var specs = new[] { new CkRollupAggregationSpec("DimmingLevel", CkRollupFunction.TimeWeightedAvg, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "DimmingLevel", AggregationFunctionDto.TimeWeightedAvg);

        Assert.NotNull(result);
        Assert.Equal(
            "SUM(\"dimminglevel_twavg_integral\") / NULLIF(SUM(\"dimminglevel_twavg_duration\"), 0)",
            result!.SqlExpression);
        Assert.Equal("dimminglevel_twavg", result.SqlAlias);
        Assert.Equal("DimmingLevel", result.OutputColumnName);
    }

    [Fact]
    public void AvgSpec_TargetTimeWeightedAvg_IsUnresolvable()
    {
        // Sample-weighted AVG must never satisfy a time-weighted request — that is the exact
        // wrong-number bug AB#4336 exists to fix.
        var specs = new[] { new CkRollupAggregationSpec("DimmingLevel", CkRollupFunction.Avg, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "DimmingLevel", AggregationFunctionDto.TimeWeightedAvg);

        Assert.Null(result);
    }

    [Fact]
    public void TimeWeightedAvgSpec_TargetSum_IsUnresolvable()
    {
        // The integral is value*ms, not a value sum.
        var specs = new[] { new CkRollupAggregationSpec("DimmingLevel", CkRollupFunction.TimeWeightedAvg, null) };

        var result = RollupQueryAggregationResolver.Resolve(specs, "DimmingLevel", AggregationFunctionDto.Sum);

        Assert.Null(result);
    }
}
