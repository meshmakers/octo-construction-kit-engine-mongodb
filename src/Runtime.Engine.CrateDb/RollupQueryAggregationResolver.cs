using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Result of resolving a target (attribute path, aggregation function) tuple against a rollup
/// archive's source-aggregation specs. Carries the chain-aware SELECT-clause SQL fragment plus
/// the alias the result row should expose. Returned as null when no compatible source
/// aggregation exists (e.g. target MIN over a rollup that only materialised AVG of the path).
/// </summary>
/// <param name="SqlExpression">
/// SELECT-clause fragment without alias, e.g.
/// <c>SUM("temperature_avg_sum") / NULLIF(SUM("temperature_avg_count"), 0)</c>.
/// </param>
/// <param name="SqlAlias">
/// Unique SQL alias used to identify the column in the result row (e.g. <c>temperature_avg</c>).
/// </param>
/// <param name="OutputColumnName">
/// User-facing column name the caller requested (echo of the input attribute path); used as the
/// dictionary key in the row mapper so the result reads back as the caller expects.
/// </param>
public sealed record RollupQueryAggregation(
    string SqlExpression,
    string SqlAlias,
    string OutputColumnName);

/// <summary>
/// Chain-aware aggregation resolver for ad-hoc queries against a <c>RollupArchive</c>. A rollup
/// stores its source aggregations as materialised columns (<c>{base}_sum</c>, <c>{base}_count</c>
/// for AVG; <c>{base}_min</c> etc. for the others). Operators querying the rollup expect to
/// supply a logical attribute path (the source path the rollup aggregates) and a target function;
/// this resolver translates that into the correct chain SQL per concept-time-range §7.
/// </summary>
/// <remarks>
/// Mapping table (target function × source aggregation):
/// <list type="table">
/// <item><c>SUM</c> over source SUM/AVG (materialised <c>_sum</c>): <c>SUM(_sum)</c></item>
/// <item><c>COUNT</c> over source COUNT/AVG (materialised <c>_count</c>): <c>SUM(_count)</c> — yes, sum of counts</item>
/// <item><c>AVG</c> over source AVG: <c>SUM(_sum) / NULLIF(SUM(_count), 0)</c></item>
/// <item><c>MIN</c> over source MIN: <c>MIN(_min)</c></item>
/// <item><c>MAX</c> over source MAX: <c>MAX(_max)</c></item>
/// </list>
/// All other combinations are unresolvable — the rollup's materialisation discarded the
/// information required (e.g. a rollup that only stored AVG can't reconstruct MIN/MAX).
/// </remarks>
public static class RollupQueryAggregationResolver
{
    /// <summary>
    /// Resolves a target aggregation against the rollup's source specs. Returns null when no
    /// chain mapping applies — callers should surface that to the operator as "unsupported
    /// aggregation on this rollup" rather than silently falling back to a wrong result.
    /// </summary>
    /// <param name="rollupAggregations">
    /// The rollup's source aggregation specs (<see cref="ArchiveSnapshot.RollupAggregations"/>).
    /// May be empty; the resolver returns null in that case.
    /// </param>
    /// <param name="targetAttributePath">Logical source path the operator queries (e.g. <c>Temperature</c>).</param>
    /// <param name="targetFunction">Aggregation function the operator wants applied.</param>
    public static RollupQueryAggregation? Resolve(
        IReadOnlyList<CkRollupAggregationSpec> rollupAggregations,
        string targetAttributePath,
        AggregationFunctionDto targetFunction)
    {
        if (rollupAggregations.Count == 0 || string.IsNullOrWhiteSpace(targetAttributePath))
        {
            return null;
        }

        // Find every rollup spec whose source path matches the requested path. Case-insensitive
        // because attribute paths come from CK YAML / GraphQL projections which may differ in
        // casing from the operator's input.
        var matchingSpecs = new List<CkRollupAggregationSpec>();
        foreach (var spec in rollupAggregations)
        {
            if (string.Equals(spec.SourcePath, targetAttributePath, StringComparison.OrdinalIgnoreCase))
            {
                matchingSpecs.Add(spec);
            }
        }
        if (matchingSpecs.Count == 0)
        {
            return null;
        }

        // For each candidate spec, try to derive the chain SQL for the requested target function.
        // Order matters when multiple specs cover the same path with different functions (e.g.
        // an MIN spec and an AVG spec on the same source): the first compatible match wins. We
        // iterate the candidate list in declaration order so the operator's CK-side ordering of
        // specs is the tie-breaker.
        foreach (var spec in matchingSpecs)
        {
            var resolved = TryResolveSingleSpec(spec, targetFunction, targetAttributePath);
            if (resolved != null)
            {
                return resolved;
            }
        }
        return null;
    }

    private static RollupQueryAggregation? TryResolveSingleSpec(
        CkRollupAggregationSpec spec,
        AggregationFunctionDto target,
        string outputColumnName)
    {
        // Re-derive the materialised column names exactly as RollupColumnGenerator / the DDL
        // emitted them — same code path the orchestrator's INSERT SQL uses, so we don't drift.
        var (_, targetColumns) = RollupAggregationColumns.Resolve(spec);

        return (spec.Function, target) switch
        {
            // Source AVG materialised as _sum + _count → these are the two columns.
            (CkRollupFunction.Avg, AggregationFunctionDto.Avg) when targetColumns.Count == 2
                => new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{targetColumns[0].ColumnName}\") / NULLIF(SUM(\"{targetColumns[1].ColumnName}\"), 0)",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_avg",
                    OutputColumnName: outputColumnName),

            (CkRollupFunction.Avg, AggregationFunctionDto.Sum) when targetColumns.Count == 2
                => new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{targetColumns[0].ColumnName}\")",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_sum",
                    OutputColumnName: outputColumnName),

            (CkRollupFunction.Avg, AggregationFunctionDto.Count) when targetColumns.Count == 2
                => new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{targetColumns[1].ColumnName}\")",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_count",
                    OutputColumnName: outputColumnName),

            // Single-column source aggregations: only same-function chaining is well-defined.
            // Target SUM over source SUM is `SUM(_sum)`; target COUNT over source COUNT is
            // `SUM(_count)` (sum of partial counts). Target MIN/MAX over the matching source.
            (CkRollupFunction.Sum, AggregationFunctionDto.Sum) when targetColumns.Count == 1
                => new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{targetColumns[0].ColumnName}\")",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_sum",
                    OutputColumnName: outputColumnName),

            (CkRollupFunction.Count, AggregationFunctionDto.Count) when targetColumns.Count == 1
                => new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{targetColumns[0].ColumnName}\")",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_count",
                    OutputColumnName: outputColumnName),

            (CkRollupFunction.Min, AggregationFunctionDto.Min) when targetColumns.Count == 1
                => new RollupQueryAggregation(
                    SqlExpression: $"MIN(\"{targetColumns[0].ColumnName}\")",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_min",
                    OutputColumnName: outputColumnName),

            (CkRollupFunction.Max, AggregationFunctionDto.Max) when targetColumns.Count == 1
                => new RollupQueryAggregation(
                    SqlExpression: $"MAX(\"{targetColumns[0].ColumnName}\")",
                    SqlAlias: $"{outputColumnName.ToLowerInvariant()}_max",
                    OutputColumnName: outputColumnName),

            _ => null,
        };
    }
}
