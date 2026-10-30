using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// Type rules for rollup-archive DDL. The names emitted by RollupColumnGenerator are storage
// identifiers (e.g. "temperature_avg_sum"), not CK-type attribute paths, so the resolver derives
// the SQL type from the aggregation function instead of walking the CK model.
public class RollupColumnTypeResolverTests
{
    [Fact]
    public void Resolve_CountAggregation_EmitsBigint()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("temperature", CkRollupFunction.Count, null) };
        var columns = RollupColumnGenerator.Generate(aggregations);

        var resolved = RollupColumnTypeResolver.Resolve(columns, aggregations);

        Assert.Single(resolved);
        Assert.Equal("temperature_count", resolved[0].Path);
        var primitive = Assert.IsType<CrateColumnType.Primitive>(resolved[0].Type);
        Assert.Equal("BIGINT", primitive.CrateTypeName);
    }

    [Fact]
    public void Resolve_AvgAggregation_EmitsDoubleSumAndBigintCount()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("temperature", CkRollupFunction.Avg, null) };
        var columns = RollupColumnGenerator.Generate(aggregations);

        var resolved = RollupColumnTypeResolver.Resolve(columns, aggregations);

        Assert.Equal(2, resolved.Count);
        var sum = Assert.IsType<CrateColumnType.Primitive>(resolved[0].Type);
        var count = Assert.IsType<CrateColumnType.Primitive>(resolved[1].Type);
        Assert.Equal("temperature_avg_sum", resolved[0].Path);
        Assert.Equal("DOUBLE PRECISION", sum.CrateTypeName);
        Assert.Equal("temperature_avg_count", resolved[1].Path);
        Assert.Equal("BIGINT", count.CrateTypeName);
    }

    [Theory]
    [InlineData(CkRollupFunction.Sum, "temperature_sum")]
    [InlineData(CkRollupFunction.Min, "temperature_min")]
    [InlineData(CkRollupFunction.Max, "temperature_max")]
    public void Resolve_NumericAggregations_EmitDouble(CkRollupFunction function, string expectedName)
    {
        var aggregations = new[] { new CkRollupAggregationSpec("temperature", function, null) };
        var columns = RollupColumnGenerator.Generate(aggregations);

        var resolved = RollupColumnTypeResolver.Resolve(columns, aggregations);

        Assert.Single(resolved);
        Assert.Equal(expectedName, resolved[0].Path);
        var primitive = Assert.IsType<CrateColumnType.Primitive>(resolved[0].Type);
        Assert.Equal("DOUBLE PRECISION", primitive.CrateTypeName);
    }

    [Fact]
    public void Resolve_ExplicitTargetColumnName_RespectedAndStillTypedByFunction()
    {
        // Pinned regression for the issue that surfaced in the studio: the activation path
        // resolved 'temperature_avg_sum' against the CK type's attributes and failed because
        // the derived storage names don't exist on the CK type. This test verifies the rollup
        // resolver handles the explicit-targetColumnName case the same way as the default-name
        // case — by deriving the type from the function, never from the CK model.
        var aggregations = new[] { new CkRollupAggregationSpec("voltage.reading", CkRollupFunction.Avg, "v_reading") };
        var columns = RollupColumnGenerator.Generate(aggregations);

        var resolved = RollupColumnTypeResolver.Resolve(columns, aggregations);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("v_reading_sum", resolved[0].Path);
        Assert.Equal("v_reading_count", resolved[1].Path);
    }

    [Fact]
    public void Resolve_EmptyColumns_ReturnsEmpty()
    {
        var resolved = RollupColumnTypeResolver.Resolve(
            Array.Empty<CkArchiveColumnSpec>(),
            new[] { new CkRollupAggregationSpec("temperature", CkRollupFunction.Sum, null) });

        Assert.Empty(resolved);
    }

    [Fact]
    public void Resolve_ColumnsAndAggregationsOutOfSync_Throws()
    {
        // Defensive: when the columns list contains a name that's not produced by any aggregation,
        // surface a clear error rather than silently emitting the wrong type.
        var columns = new[] { new CkArchiveColumnSpec("orphan_column", Indexed: true, Required: false) };
        var aggregations = new[] { new CkRollupAggregationSpec("temperature", CkRollupFunction.Sum, null) };

        Assert.Throws<UnresolvableArchivePathException>(
            () => RollupColumnTypeResolver.Resolve(columns, aggregations));
    }

    [Fact]
    public void Resolve_RollupInternalComputedColumn_TypedFromResultType()
    {
        // A rollup may declare a computed column over its aggregate outputs (concept §11). It is not
        // backed by an aggregation, so the resolver types it from ResultType — nullable, like raw.
        var aggregations = new[] { new CkRollupAggregationSpec("active", CkRollupFunction.Sum, null) };
        var columns = new List<CkArchiveColumnSpec>(RollupColumnGenerator.Generate(aggregations))
        {
            new(string.Empty, Indexed: true, Required: false)
            {
                Name = "ratio",
                Formula = "active_sum / 2",
                ResultType = FormulaResultType.Double,
            },
        };

        var resolved = RollupColumnTypeResolver.Resolve(columns, aggregations);

        Assert.Equal(2, resolved.Count); // active_sum + ratio
        var computed = resolved[1];
        Assert.Equal("ratio", computed.ColumnName);
        Assert.Equal("DOUBLE PRECISION", Assert.IsType<CrateColumnType.Primitive>(computed.Type).CrateTypeName);
        Assert.False(computed.Required);
    }

    [Fact]
    public void Resolve_TimeWeightedAvgAggregation_EmitsDoubleIntegralAndBigintDuration()
    {
        // AB#4336: TWA materialises as (integral, duration) — integral is value*ms (DOUBLE),
        // duration is covered milliseconds (BIGINT). Same suffix fork as AVG's sum/count.
        var aggregations = new[] { new CkRollupAggregationSpec("dimminglevel", CkRollupFunction.TimeWeightedAvg, null) };
        var columns = RollupColumnGenerator.Generate(aggregations);

        var resolved = RollupColumnTypeResolver.Resolve(columns, aggregations);

        Assert.Equal(2, resolved.Count);
        var integral = Assert.IsType<CrateColumnType.Primitive>(resolved[0].Type);
        var duration = Assert.IsType<CrateColumnType.Primitive>(resolved[1].Type);
        Assert.Equal("dimminglevel_twavg_integral", resolved[0].Path);
        Assert.Equal("DOUBLE PRECISION", integral.CrateTypeName);
        Assert.Equal("dimminglevel_twavg_duration", resolved[1].Path);
        Assert.Equal("BIGINT", duration.CrateTypeName);
    }
}
