using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// One target storage column produced from a <see cref="CkRollupAggregationSpec"/>: the lower-cased
/// CrateDB column name plus the SQL aggregate function applied to the source column.
/// Rollup-archives concept §5.
/// </summary>
/// <param name="ColumnName">Lower-cased CrateDB column name. PK columns are not represented here.</param>
/// <param name="Function">SQL aggregate keyword: <c>SUM</c>, <c>MIN</c>, <c>MAX</c>, <c>COUNT</c>.</param>
internal sealed record RollupTargetColumn(string ColumnName, string Function);

/// <summary>
/// Resolves <see cref="CkRollupAggregationSpec"/> entries to the concrete CrateDB target columns
/// that the DDL generator must emit and the SQL builder must populate. Pure function. <c>AVG</c>
/// materialises as two columns (<c>{base}_sum</c> + <c>{base}_count</c>) so chained rollups stay
/// numerically correct — the average is recomputed on read as <c>sum / NULLIF(count, 0)</c>.
/// Rollup-archives concept §5, §7.
/// </summary>
internal static class RollupAggregationColumns
{
    /// <summary>
    /// Returns the target storage columns plus the source column name (lower-cased, derived from
    /// <see cref="CkRollupAggregationSpec.SourcePath"/>) for one aggregation spec. The source
    /// column name is the same value the DDL generator picked for the source archive — both go
    /// through <see cref="ColumnNameMapper.PathToColumnName"/>.
    /// </summary>
    public static (string SourceColumn, IReadOnlyList<RollupTargetColumn> Targets) Resolve(CkRollupAggregationSpec spec)
    {
        var sourceColumn = ColumnNameMapper.PathToColumnName(spec.SourcePath);
        var baseName = !string.IsNullOrWhiteSpace(spec.TargetColumnName)
            ? spec.TargetColumnName!.ToLowerInvariant()
            : $"{sourceColumn}_{spec.Function.ToString().ToLowerInvariant()}";

        var targets = spec.Function switch
        {
            CkRollupFunction.Avg => new[]
            {
                new RollupTargetColumn($"{baseName}_sum", "SUM"),
                new RollupTargetColumn($"{baseName}_count", "COUNT"),
            },
            CkRollupFunction.Min => new[] { new RollupTargetColumn(baseName, "MIN") },
            CkRollupFunction.Max => new[] { new RollupTargetColumn(baseName, "MAX") },
            CkRollupFunction.Sum => new[] { new RollupTargetColumn(baseName, "SUM") },
            CkRollupFunction.Count => new[] { new RollupTargetColumn(baseName, "COUNT") },
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(spec), spec.Function, "Unknown rollup function.")
        };

        return (sourceColumn, targets);
    }
}
