using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Role of a physical column when its origin function materialises as a ratio pair — AVG's
/// <c>(sum, count)</c> and TimeWeightedAvg's <c>(integral, duration)</c> (AB#4336). The chain
/// walker has to track which slot a given physical column fills so the resolver can recombine
/// them for the ratio target (and, for AVG, the SUM / COUNT component targets).
/// </summary>
public enum LogicalAvgRole
{
    /// <summary>Holds the numerator partial (AVG's sum / TWA's integral).</summary>
    Numerator,
    /// <summary>Holds the denominator partial (AVG's count / TWA's covered duration).</summary>
    Denominator
}

/// <summary>
/// One physical storage column on a rollup table, tagged with the original CK-attribute path
/// and the logical aggregation function that ultimately produces it. For cascade rollups the
/// chain walker propagates this label forward across each level so the resolver can answer
/// "given target (path, function), which physical column on the current rollup do I read?".
/// </summary>
/// <param name="LogicalPath">Original CK attribute path the rollup chain aggregates over.</param>
/// <param name="LogicalFunction">Original aggregation function the chain ultimately materialises.</param>
/// <param name="PhysicalColumnName">Column name on the *current* rollup table (lower-cased CrateDB identifier).</param>
/// <param name="AvgRole">Set only when <see cref="LogicalFunction"/> = AVG to distinguish the sum and count slots.</param>
public sealed record LogicalOriginColumn(
    string LogicalPath,
    CkRollupFunction LogicalFunction,
    string PhysicalColumnName,
    LogicalAvgRole? AvgRole);

/// <summary>
/// Chain-aware aggregation resolver for ad-hoc queries against a (possibly cascade) rollup
/// archive. Walks the rollup's source-archive chain to recover the lineage of each physical
/// storage column, then maps a target (path, function) to the right column + SQL fragment on
/// the *top-level* rollup we're querying.
/// </summary>
/// <remarks>
/// Generalises <see cref="RollupQueryAggregationResolver"/> to arbitrary chain depths.
/// For a 1-level rollup (rollup-on-raw / rollup-on-time-range) the two resolvers produce
/// equivalent output. For N-level cascades (rollup-on-rollup-on-…-on-raw) the chain walker
/// propagates the logical (path, function) label forward across each level, applying the
/// concept-time-range §7 chain rules at each step:
///
/// - <c>SUM</c> chains as sum-of-sums → <c>SUM</c>
/// - <c>COUNT</c> chains via sum-of-counts → <c>COUNT</c>
/// - <c>MIN</c> chains as min-of-mins → <c>MIN</c>
/// - <c>MAX</c> chains as max-of-maxes → <c>MAX</c>
/// - <c>AVG</c> chains as sum-of-sums / sum-of-counts → <c>AVG</c> (both pair components propagate)
///
/// Anything else (e.g. trying to chain MIN over an AVG-materialised parent) is unresolvable
/// and produces null — same fail-soft contract as the 1-level resolver.
/// </remarks>
public static class RollupChainAggregationResolver
{
    /// <summary>
    /// Resolves a target (path, function) against the rollup's chain. Returns null when no
    /// origin in the chain produces the requested logical aggregation, or when the chain
    /// itself is broken (missing parent in store).
    /// </summary>
    /// <param name="rollup">The rollup archive snapshot being queried.</param>
    /// <param name="targetAttributePath">Logical CK attribute path the operator queries.</param>
    /// <param name="targetFunction">Aggregation function the operator wants applied.</param>
    /// <param name="getArchive">Loader for any archive snapshot by rtId. Returns null if missing.</param>
    /// <param name="getRollup">Loader for a rollup snapshot by rtId. Returns null if the archive isn't a rollup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<RollupQueryAggregation?> ResolveAsync(
        RollupArchiveSnapshot rollup,
        string targetAttributePath,
        AggregationFunctionDto targetFunction,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetAttributePath))
        {
            return null;
        }

        var origins = await BuildOriginsAsync(rollup, getArchive, getRollup, cancellationToken).ConfigureAwait(false);
        if (origins.Count == 0)
        {
            return null;
        }

        var matching = origins
            .Where(o => string.Equals(o.LogicalPath, targetAttributePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        return BuildTargetAggregation(matching, targetAttributePath, targetFunction);
    }

    /// <summary>
    /// Recursive chain walker. For a rollup over raw / time-range, the rollup's specs already
    /// reference logical paths — origins are derived directly. For a rollup over another rollup,
    /// we recurse: source origins tell us what each parent physical column represents, then we
    /// propagate that meaning through each spec on the current rollup, validating chain rules.
    /// </summary>
    private static async Task<IReadOnlyList<LogicalOriginColumn>> BuildOriginsAsync(
        RollupArchiveSnapshot rollup,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceSnapshot = await getArchive(rollup.SourceArchiveRtId).ConfigureAwait(false);
        if (sourceSnapshot is null)
        {
            return Array.Empty<LogicalOriginColumn>();
        }

        if (sourceSnapshot.RollupAggregations is null)
        {
            // Direct rollup over raw / time-range — sourcePath is the logical CK attribute path.
            return BuildDirectOrigins(rollup);
        }

        // Cascade rollup — recurse for source's origins, then propagate.
        var sourceRollup = await getRollup(rollup.SourceArchiveRtId).ConfigureAwait(false);
        if (sourceRollup is null)
        {
            return Array.Empty<LogicalOriginColumn>();
        }

        var sourceOrigins = await BuildOriginsAsync(sourceRollup, getArchive, getRollup, cancellationToken).ConfigureAwait(false);
        return BuildCascadeOrigins(rollup, sourceOrigins);
    }

    private static List<LogicalOriginColumn> BuildDirectOrigins(RollupArchiveSnapshot rollup)
    {
        var result = new List<LogicalOriginColumn>();
        foreach (var spec in rollup.Aggregations)
        {
            var (_, targets) = RollupAggregationColumns.Resolve(spec);
            if (spec.Function is CkRollupFunction.Avg or CkRollupFunction.TimeWeightedAvg && targets.Count == 2)
            {
                // Ratio-pair materialisation — AVG's (_sum, _count) / TWA's (_integral, _duration):
                // track both slots so the resolver can recombine them.
                result.Add(new LogicalOriginColumn(
                    spec.SourcePath, spec.Function, targets[0].ColumnName, LogicalAvgRole.Numerator));
                result.Add(new LogicalOriginColumn(
                    spec.SourcePath, spec.Function, targets[1].ColumnName, LogicalAvgRole.Denominator));
            }
            else
            {
                foreach (var target in targets)
                {
                    result.Add(new LogicalOriginColumn(
                        spec.SourcePath, spec.Function, target.ColumnName, null));
                }
            }
        }
        return result;
    }

    private static List<LogicalOriginColumn> BuildCascadeOrigins(
        RollupArchiveSnapshot rollup,
        IReadOnlyList<LogicalOriginColumn> sourceOrigins)
    {
        // Build a quick lookup: parent physical column → its lineage.
        var sourceByColumn = new Dictionary<string, LogicalOriginColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in sourceOrigins)
        {
            // First-seen wins — duplicates would indicate an inconsistent parent and we just
            // take the first matching label.
            sourceByColumn.TryAdd(o.PhysicalColumnName, o);
        }

        var result = new List<LogicalOriginColumn>();
        foreach (var spec in rollup.Aggregations)
        {
            // This rollup's spec reads spec.SourcePath (a physical column on the parent) and
            // applies spec.Function on it to produce this rollup's target columns.
            var parentColumnName = ColumnNameMapper.PathToColumnName(spec.SourcePath);
            if (!sourceByColumn.TryGetValue(parentColumnName, out var parentOrigin)
                && !sourceByColumn.TryGetValue(spec.SourcePath, out parentOrigin))
            {
                // Source path on this rollup's spec doesn't resolve to any parent column —
                // chain is broken (or the parent was edited after this rollup was provisioned).
                continue;
            }

            // Chain rule: combine the parent's logical function with the function this rollup
            // applies. The result is what the cascade aggregation logically represents.
            var (chainedFunction, chainedAvgRole) = ChainFunction(parentOrigin, spec.Function);
            if (chainedFunction is null)
            {
                continue; // unsupported chain combination
            }

            var (_, targets) = RollupAggregationColumns.Resolve(spec);
            foreach (var target in targets)
            {
                result.Add(new LogicalOriginColumn(
                    parentOrigin.LogicalPath,
                    chainedFunction.Value,
                    target.ColumnName,
                    chainedAvgRole));
            }
        }
        return result;
    }

    /// <summary>
    /// Single-step chain rule lookup. Given the parent's logical (function + AVG role) and the
    /// function this rollup applies on top, what's the resulting logical function + role?
    /// Returns (null, null) for combinations that are not legal aggregation chains.
    /// </summary>
    private static (CkRollupFunction? Function, LogicalAvgRole? AvgRole) ChainFunction(
        LogicalOriginColumn parent,
        CkRollupFunction applied)
    {
        // Ratio-pair materialisation (AVG's sum/count, TWA's integral/duration): both slots behave
        // like SUM accumulators under cascading — summing partial numerators / denominators keeps
        // the recombined ratio exact. The parent's logical function (AVG or TWA) is preserved.
        if (parent.AvgRole == LogicalAvgRole.Numerator)
        {
            return applied == CkRollupFunction.Sum
                ? (parent.LogicalFunction, LogicalAvgRole.Numerator)
                : (null, null);
        }
        if (parent.AvgRole == LogicalAvgRole.Denominator)
        {
            return applied == CkRollupFunction.Sum
                ? (parent.LogicalFunction, LogicalAvgRole.Denominator)
                : (null, null);
        }

        // Single-column origins: only same-function chaining is valid, except for COUNT which
        // chains via SUM-of-counts (the orchestrator's rollup-of-rollup spec stores the parent's
        // _count column with function = SUM so the chain rule preserves the COUNT semantics).
        return (parent.LogicalFunction, applied) switch
        {
            (CkRollupFunction.Sum, CkRollupFunction.Sum) => (CkRollupFunction.Sum, null),
            (CkRollupFunction.Count, CkRollupFunction.Sum) => (CkRollupFunction.Count, null),
            (CkRollupFunction.Min, CkRollupFunction.Min) => (CkRollupFunction.Min, null),
            (CkRollupFunction.Max, CkRollupFunction.Max) => (CkRollupFunction.Max, null),
            // Absolute durations accumulate — a cascade re-aggregates the ms column with SUM
            // and the result is still a StateDuration (AB#4336).
            (CkRollupFunction.StateDuration, CkRollupFunction.Sum) => (CkRollupFunction.StateDuration, null),
            _ => (null, null),
        };
    }

    /// <summary>
    /// Maps the matching origins for a (logical path) to a SELECT-clause fragment for the
    /// requested target function. Implements the read-side chain SQL: SUM-of-stored for SUM,
    /// SUM-of-stored for COUNT (the rollup stores partial counts), MIN/MAX-of-stored for
    /// MIN/MAX, and SUM(numerator)/NULLIF(SUM(denominator), 0) for AVG — built either from a
    /// materialised AVG pair or from a separately materialised SUM + COUNT pair.
    /// </summary>
    private static RollupQueryAggregation? BuildTargetAggregation(
        IReadOnlyList<LogicalOriginColumn> matching,
        string outputColumnName,
        AggregationFunctionDto targetFunction)
    {
        var alias = outputColumnName.ToLowerInvariant();

        switch (targetFunction)
        {
            case AggregationFunctionDto.Sum:
            {
                // Prefer a single-column SUM origin; fall back to the AVG-numerator slot, then to
                // a StateDuration origin (total ms-in-state over the window, AB#4336).
                var origin = FindSingle(matching, CkRollupFunction.Sum)
                    ?? FindAvgRole(matching, LogicalAvgRole.Numerator)
                    ?? FindSingle(matching, CkRollupFunction.StateDuration);
                if (origin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{origin.PhysicalColumnName}\")",
                    SqlAlias: $"{alias}_sum",
                    OutputColumnName: outputColumnName);
            }
            case AggregationFunctionDto.Count:
            {
                var origin = FindSingle(matching, CkRollupFunction.Count)
                    ?? FindAvgRole(matching, LogicalAvgRole.Denominator);
                if (origin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{origin.PhysicalColumnName}\")",
                    SqlAlias: $"{alias}_count",
                    OutputColumnName: outputColumnName);
            }
            case AggregationFunctionDto.Min:
            {
                var origin = FindSingle(matching, CkRollupFunction.Min);
                if (origin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"MIN(\"{origin.PhysicalColumnName}\")",
                    SqlAlias: $"{alias}_min",
                    OutputColumnName: outputColumnName);
            }
            case AggregationFunctionDto.Max:
            {
                var origin = FindSingle(matching, CkRollupFunction.Max);
                if (origin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"MAX(\"{origin.PhysicalColumnName}\")",
                    SqlAlias: $"{alias}_max",
                    OutputColumnName: outputColumnName);
            }
            case AggregationFunctionDto.Avg:
            {
                // Prefer the separately materialised SUM + COUNT pair over the materialised
                // AVG pair: mathematically equivalent, but historically more reliable. The
                // AVG-pair columns can be null on real-world rollups (e.g. when the AVG spec
                // was added after the table was first populated and a re-aggregation never
                // happened), whereas the basic SUM + COUNT specs are typically present from
                // the rollup's first orchestrator tick.
                var sumOrigin = FindSingle(matching, CkRollupFunction.Sum)
                    ?? FindAvgRole(matching, LogicalAvgRole.Numerator);
                var countOrigin = FindSingle(matching, CkRollupFunction.Count)
                    ?? FindAvgRole(matching, LogicalAvgRole.Denominator);
                if (sumOrigin is null || countOrigin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{sumOrigin.PhysicalColumnName}\") / NULLIF(SUM(\"{countOrigin.PhysicalColumnName}\"), 0)",
                    SqlAlias: $"{alias}_avg",
                    OutputColumnName: outputColumnName);
            }
            case AggregationFunctionDto.StateDuration:
            {
                // Total ms-in-state over the window: SUM of the (possibly SUM-cascaded)
                // per-bucket durations (AB#4336 / AB#4341).
                var origin = FindSingle(matching, CkRollupFunction.StateDuration);
                if (origin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{origin.PhysicalColumnName}\")",
                    SqlAlias: $"{alias}_stateduration",
                    OutputColumnName: outputColumnName);
            }
            case AggregationFunctionDto.TimeWeightedAvg:
            {
                // TWA recombines exclusively from its own materialised (integral, duration) pair —
                // unlike AVG there is no single-column fallback, and the AVG pair must not satisfy
                // a TWA target (sample-weighted ≠ time-weighted). AB#4336.
                var integralOrigin = FindPairRole(matching, CkRollupFunction.TimeWeightedAvg, LogicalAvgRole.Numerator);
                var durationOrigin = FindPairRole(matching, CkRollupFunction.TimeWeightedAvg, LogicalAvgRole.Denominator);
                if (integralOrigin is null || durationOrigin is null) return null;
                return new RollupQueryAggregation(
                    SqlExpression: $"SUM(\"{integralOrigin.PhysicalColumnName}\") / NULLIF(SUM(\"{durationOrigin.PhysicalColumnName}\"), 0)",
                    SqlAlias: $"{alias}_twavg",
                    OutputColumnName: outputColumnName);
            }
            default:
                return null;
        }
    }

    private static LogicalOriginColumn? FindSingle(IReadOnlyList<LogicalOriginColumn> origins, CkRollupFunction function)
        => origins.FirstOrDefault(o => o.LogicalFunction == function && o.AvgRole is null);

    private static LogicalOriginColumn? FindAvgRole(IReadOnlyList<LogicalOriginColumn> origins, LogicalAvgRole role)
        => FindPairRole(origins, CkRollupFunction.Avg, role);

    private static LogicalOriginColumn? FindPairRole(
        IReadOnlyList<LogicalOriginColumn> origins, CkRollupFunction function, LogicalAvgRole role)
        => origins.FirstOrDefault(o => o.LogicalFunction == function && o.AvgRole == role);
}
