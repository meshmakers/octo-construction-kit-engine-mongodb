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
        var functionToken = spec.Function == CkRollupFunction.TimeWeightedAvg
            ? "twavg" // short default-name token, matching RollupColumnGenerator (AB#4336 D5)
            : spec.Function.ToString().ToLowerInvariant();
        var baseName = !string.IsNullOrWhiteSpace(spec.TargetColumnName)
            ? spec.TargetColumnName!.ToLowerInvariant()
            : $"{sourceColumn}_{functionToken}";

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
            // TWA has no single SQL aggregate keyword — the SQL builder emits a dedicated
            // LOCF-weighted expression per target column (AB#4336). The Function tokens below are
            // markers the builder branches on, never emitted verbatim.
            CkRollupFunction.TimeWeightedAvg => new[]
            {
                new RollupTargetColumn($"{baseName}_integral", TimeWeightedIntegral),
                new RollupTargetColumn($"{baseName}_duration", TimeWeightedDuration),
            },
            // Marker like the TWA pair — the SQL builders emit a comparison-guarded duration
            // expression; the token is never emitted verbatim (AB#4336).
            CkRollupFunction.StateDuration => new[]
            {
                new RollupTargetColumn(baseName, StateDurationMarker),
            },
            // Markers — the SQL builders emit an arg_min / arg_max over time expression
            // (the value at the earliest / latest observation in the bucket); the token is never
            // emitted verbatim (AB#4188). Single DOUBLE column.
            CkRollupFunction.First => new[] { new RollupTargetColumn(baseName, FirstMarker) },
            CkRollupFunction.Last => new[] { new RollupTargetColumn(baseName, LastMarker) },
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(spec), spec.Function, "Unknown rollup function.")
        };

        return (sourceColumn, targets);
    }

    /// <summary>Marker function token for the TWA integral column (Σ value × Δt in value·ms).</summary>
    public const string TimeWeightedIntegral = "TW_INTEGRAL";

    /// <summary>Marker function token for the TWA covered-duration column (ms).</summary>
    public const string TimeWeightedDuration = "TW_DURATION";

    /// <summary>Marker function token for the StateDuration column (ms the signal held ComparisonValue).</summary>
    public const string StateDurationMarker = "STATE_DURATION";

    /// <summary>Marker function token for the First column (value at the earliest timestamp in the bucket, AB#4188).</summary>
    public const string FirstMarker = "ARG_FIRST";

    /// <summary>Marker function token for the Last column (value at the latest timestamp in the bucket, AB#4188).</summary>
    public const string LastMarker = "ARG_LAST";
}
