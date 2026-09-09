using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;

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
/// One target storage column bound to the physical source column it is computed from on <em>one
/// particular</em> source of the rollup (AB#5157 §3). The same spec binds differently per source:
/// over a base archive the source column is the spec's own path (rule 1), over a rollup source that
/// stores the same logical aggregation it is the child's target column (rule 2).
/// </summary>
/// <param name="ColumnName">Lower-cased CrateDB target column name (identical for every source).</param>
/// <param name="Function">
/// SQL aggregate keyword (<c>SUM</c>, <c>MIN</c>, <c>MAX</c>, <c>COUNT</c>) or one of the
/// <see cref="RollupAggregationColumns"/> marker tokens the SQL builder branches on.
/// </param>
/// <param name="SourceColumn">Lower-cased physical column on this source the function reads.</param>
internal sealed record RollupBoundTargetColumn(string ColumnName, string Function, string SourceColumn);

/// <summary>
/// The per-source resolution of one aggregation spec: its target columns, each bound to the source
/// column it reads on this source, plus the rule that produced the binding (AB#5157 §3).
/// </summary>
/// <param name="Spec">The parent rollup's logical aggregation spec (rides along so marker branches can reach <see cref="CkRollupAggregationSpec.ComparisonValue"/>).</param>
/// <param name="Kind">Which resolver rule matched this source.</param>
/// <param name="Columns">The bound target columns, in the stable order of <see cref="RollupAggregationColumns.Resolve"/>.</param>
internal sealed record RollupSourceAggregation(
    CkRollupAggregationSpec Spec,
    RollupSourceColumnResolutionKind Kind,
    IReadOnlyList<RollupBoundTargetColumn> Columns);

/// <summary>
/// Resolves <see cref="CkRollupAggregationSpec"/> entries to the concrete CrateDB target columns
/// that the DDL generator must emit and the SQL builder must populate. Pure function. <c>AVG</c>
/// materialises as two columns (<c>{base}_sum</c> + <c>{base}_count</c>) so chained rollups stay
/// numerically correct — the average is recomputed on read as <c>sum / NULLIF(count, 0)</c>.
/// Rollup-archives concept §5, §7.
/// </summary>
/// <remarks>
/// Two views of a spec live here. <see cref="Resolve"/> answers "which physical columns does a
/// rollup <em>store</em> for this spec" — source-independent, used by the DDL / read path. The
/// <see cref="ResolveForSource(CkRollupAggregationSpec, ArchiveSnapshot, RollupArchiveSnapshot?, OctoObjectId)"/>
/// family answers "which physical columns of <em>this source</em> does the write path read to fill
/// them", which since AB#5157 depends on the source: a multi-source rollup may read the same
/// logical spec from a time-range base archive on one validity span and from an hourly rollup of
/// it on another (the AC1 cutover shape).
/// </remarks>
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(spec), spec.Function, "Unknown rollup function.")
        };

        return (sourceColumn, targets);
    }

    /// <summary>
    /// Binds every aggregation of a rollup to the physical columns of <paramref name="source"/>
    /// (AB#5157 §3) — the write-side companion of <see cref="Resolve"/>. Called once per
    /// (rollup, source) pair by the forward aggregation and the recompute executor; the result
    /// feeds <see cref="RollupAggregationSqlBuilder"/>.
    /// </summary>
    /// <param name="aggregations">The rollup's logical aggregation specs.</param>
    /// <param name="rollupRtId">The rollup's id — only used to name it in the failure message.</param>
    /// <param name="source">The source's archive-level snapshot (its captured columns).</param>
    /// <param name="sourceRollup">
    /// The source's rollup snapshot when the source is a rollup, otherwise <c>null</c>. Without it
    /// only rule 1 (declared column) applies to that source.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A spec cannot be resolved on <paramref name="source"/> — see
    /// <see cref="ResolveForSource(CkRollupAggregationSpec, ArchiveSnapshot, RollupArchiveSnapshot?, OctoObjectId)"/>.
    /// </exception>
    public static IReadOnlyList<RollupSourceAggregation> ResolveForSource(
        IReadOnlyList<CkRollupAggregationSpec> aggregations,
        OctoObjectId rollupRtId,
        ArchiveSnapshot source,
        RollupArchiveSnapshot? sourceRollup)
    {
        ArgumentNullException.ThrowIfNull(aggregations);

        var result = new List<RollupSourceAggregation>(aggregations.Count);
        foreach (var spec in aggregations)
        {
            result.Add(ResolveForSource(spec, source, sourceRollup, rollupRtId));
        }

        return result;
    }

    /// <summary>
    /// Binds one aggregation spec to the physical columns of <paramref name="source"/> via
    /// <see cref="RollupSourceColumnResolver.TryResolve"/> (AB#5157 §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rule 1 (<see cref="RollupSourceColumnResolutionKind.DeclaredColumn"/>): the source declares
    /// <see cref="CkRollupAggregationSpec.SourcePath"/> verbatim — an ingested path, a computed
    /// column name or a rollup's physical column name (the pre-AB#5157 chained style). The binding
    /// is exactly <see cref="Resolve"/>: every target reads the single mapped source column with
    /// the function <see cref="Resolve"/> assigns, so the SQL is unchanged from before AB#5157.
    /// </para>
    /// <para>
    /// Rule 2 (<see cref="RollupSourceColumnResolutionKind.ChildAggregation"/>): the source is a
    /// rollup storing the same logical aggregation. The child's target columns
    /// (<see cref="RollupColumnGenerator.TargetColumnNamesFor"/>, honouring the child's own
    /// <see cref="CkRollupAggregationSpec.TargetColumnName"/>) are the parent's source columns, read
    /// <em>function-preserving</em> over the child windows inside the parent bucket:
    /// Sum / Count / StateDuration ⇒ <c>SUM(child)</c>; Avg ⇒ <c>SUM(child _sum)</c>,
    /// <c>SUM(child _count)</c>; TimeWeightedAvg ⇒ <c>SUM(child _integral)</c>,
    /// <c>SUM(child _duration)</c>; Min ⇒ <c>MIN(child)</c>; Max ⇒ <c>MAX(child)</c>; First / Last ⇒
    /// the child's value at the earliest / latest child window in the bucket (the
    /// <see cref="FirstMarker"/> / <see cref="LastMarker"/> branches of the SQL builder over the
    /// child's window order). Rule 1 wins when both apply.
    /// </para>
    /// </remarks>
    /// <param name="spec">The parent rollup's logical aggregation spec.</param>
    /// <param name="source">The source's archive-level snapshot (its captured columns).</param>
    /// <param name="sourceRollup">The source's rollup snapshot when the source is a rollup, otherwise <c>null</c>.</param>
    /// <param name="rollupRtId">The parent rollup's id — only used to name it in the failure message.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceRollup"/> is a different archive than <paramref name="source"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Neither rule matches. The activation validator (<c>RollupValidator</c>, rule 14) refuses
    /// exactly this case, so reaching it means the rollup or one of its sources was edited after
    /// activation; the message names the spec, the source and the rollup. The SQL builder never
    /// receives an unresolved spec, so no NULL-producing column is ever emitted silently.
    /// </exception>
    public static RollupSourceAggregation ResolveForSource(
        CkRollupAggregationSpec spec,
        ArchiveSnapshot source,
        RollupArchiveSnapshot? sourceRollup,
        OctoObjectId rollupRtId)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(source);
        if (sourceRollup is not null && sourceRollup.RtId != source.RtId)
        {
            throw new ArgumentException(
                $"sourceRollup {sourceRollup.RtId} does not belong to source archive {source.RtId}.",
                nameof(sourceRollup));
        }

        var resolution = RollupSourceColumnResolver.TryResolve(spec, source, sourceRollup);
        if (resolution is null)
        {
            var sourceKind = source.RollupAggregations is not null
                ? "rollup"
                : source.IsTimeRange ? "time-range archive" : "raw archive";
            var rule2Hint = source.RollupAggregations is not null && sourceRollup is null
                ? " (no rollup snapshot was supplied for the rollup source, so only a verbatim declared column can match)"
                : string.Empty;
            throw new InvalidOperationException(
                $"Rollup {rollupRtId}: aggregation (SourcePath '{spec.SourcePath}', {spec.Function}) cannot be resolved " +
                $"on source {sourceKind} {source.RtId}{rule2Hint}: the source neither declares '{spec.SourcePath}' " +
                $"as a column nor stores the same {spec.Function} aggregation of '{RollupSourceColumnResolver.NormalisePath(spec.SourcePath)}'. " +
                "Activation validation refuses this configuration — the rollup or its source was changed after activation.");
        }

        return resolution.Kind == RollupSourceColumnResolutionKind.DeclaredColumn
            ? BindDeclared(spec)
            : BindChildAggregation(spec, resolution.ChildAggregation!, source.RtId, rollupRtId);
    }

    /// <summary>
    /// The rule-1 binding of <paramref name="spec"/>: every target column of <see cref="Resolve"/>
    /// reads the spec's own mapped source column with the function <see cref="Resolve"/> assigns.
    /// Also the binding a base (raw / time-range) source always gets.
    /// </summary>
    public static RollupSourceAggregation BindDeclared(CkRollupAggregationSpec spec)
    {
        var (sourceColumn, targets) = Resolve(spec);
        var columns = new RollupBoundTargetColumn[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            columns[i] = new RollupBoundTargetColumn(targets[i].ColumnName, targets[i].Function, sourceColumn);
        }

        return new RollupSourceAggregation(spec, RollupSourceColumnResolutionKind.DeclaredColumn, columns);
    }

    /// <summary>
    /// The rule-2 binding: the parent's target columns read the child's target columns
    /// function-preserving (see the remarks on
    /// <see cref="ResolveForSource(CkRollupAggregationSpec, ArchiveSnapshot, RollupArchiveSnapshot?, OctoObjectId)"/>).
    /// </summary>
    private static RollupSourceAggregation BindChildAggregation(
        CkRollupAggregationSpec spec, CkRollupAggregationSpec child, OctoObjectId sourceRtId, OctoObjectId rollupRtId)
    {
        var (_, targets) = Resolve(spec);
        var childColumns = RollupColumnGenerator.TargetColumnNamesFor(child).ToList();
        if (childColumns.Count != targets.Count)
        {
            // Same function ⇒ same column arity; anything else is a resolver / generator drift.
            throw new InvalidOperationException(
                $"Rollup {rollupRtId}: aggregation (SourcePath '{spec.SourcePath}', {spec.Function}) resolved to child " +
                $"aggregation (SourcePath '{child.SourcePath}', {child.Function}) of rollup source {sourceRtId}, but the child " +
                $"stores {childColumns.Count} column(s) where the parent expects {targets.Count}.");
        }

        // StateDuration measures the time spent in one specific state; summing a child that measured
        // a different literal would silently produce wrong durations. The engine-side resolver
        // already refuses the mismatch — this is the defensive twin so the SQL layer can never emit
        // such a read.
        if (spec.Function == CkRollupFunction.StateDuration
            && !string.Equals(spec.ComparisonValue?.Trim(), child.ComparisonValue?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Rollup {rollupRtId}: StateDuration aggregation on '{spec.SourcePath}' compares '{spec.ComparisonValue}', " +
                $"but the matched child aggregation of rollup source {sourceRtId} compares '{child.ComparisonValue}'.");
        }

        // Function-preserving read over the child windows inside the bucket: additive functions
        // (and the ratio pairs' components) sum up, Min / Max nest, First / Last pick the child's
        // value at the earliest / latest child window.
        var function = spec.Function switch
        {
            CkRollupFunction.Sum => "SUM",
            CkRollupFunction.Count => "SUM",
            CkRollupFunction.Avg => "SUM",
            CkRollupFunction.TimeWeightedAvg => "SUM",
            CkRollupFunction.StateDuration => "SUM",
            CkRollupFunction.Min => "MIN",
            CkRollupFunction.Max => "MAX",
            CkRollupFunction.First => FirstMarker,
            CkRollupFunction.Last => LastMarker,
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Function, "Unknown rollup function."),
        };

        var columns = new RollupBoundTargetColumn[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            columns[i] = new RollupBoundTargetColumn(targets[i].ColumnName, function, childColumns[i]);
        }

        return new RollupSourceAggregation(spec, RollupSourceColumnResolutionKind.ChildAggregation, columns);
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
