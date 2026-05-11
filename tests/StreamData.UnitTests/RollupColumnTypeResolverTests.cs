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
}
