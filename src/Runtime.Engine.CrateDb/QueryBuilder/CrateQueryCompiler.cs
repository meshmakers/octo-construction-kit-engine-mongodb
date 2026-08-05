using System.Text;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// Crate Query Compiler
/// </summary>
internal class CrateQueryCompiler
{
    /// <summary>
    /// Compiles the query
    /// </summary>
    /// <param name="queryBuilder"></param>
    /// <returns></returns>
    public string CompileQuery(CrateQueryBuilder queryBuilder)
    {
        // Downsampling with aggregation uses a fundamentally different SQL structure
        // with generate_series LEFT JOIN to produce all time bins including empty ones
        if (queryBuilder.QueryMode == QueryModeDto.Downsampling && queryBuilder.HasAggregations)
        {
            return CompileDownsamplingQuery(queryBuilder);
        }

        var query = new StringBuilder();

        query.Append("SELECT ");

        // figure out if we want to downsample or interpolate

        if (queryBuilder.QueryMode == QueryModeDto.Downsampling)
        {
            if (queryBuilder.TimeStampVariable == null)
            {
                throw QueryBuilderException.InterpolationOrDownsamplingNeedToIncludeTimeStampVariable();
            }

            var interval = queryBuilder.To!.Value - queryBuilder.From!.Value;
            var intervalSeconds = (int)interval.TotalSeconds / queryBuilder.Limit;

            query.Append($"DATE_BIN('{intervalSeconds} seconds'::INTERVAL, \"{queryBuilder.TimeColumn}\", 0) AS \"T\", ");
        }
        else if(queryBuilder.TimeStampVariable != null)
        {
            var timeStampVariable = queryBuilder.TimeStampVariable;
            query.Append(timeStampVariable.ToSelectString() + ", ");
        }

        var queryVariables = string.Join(", ", queryBuilder.QueryVariablesWithoutTimestamp.Select(x => x.ToSelectString()));
        query.Append(queryVariables);

        query.Append($" FROM {queryBuilder.TenantId}");

        AppendWhereClause(query, queryBuilder);

        if (queryBuilder.HasAggregations && queryBuilder.Groupings.Any())
        {
            query.Append(" GROUP BY ");
            query.Append(string.Join(", ", queryBuilder.Groupings.Select(x => x.ToGroupByString())));
        }

        if (queryBuilder.HasOrderBy)
        {
            query.Append(" ORDER BY ");
            query.Append(string.Join(", ", queryBuilder.OrderByVariables.Select(x => x.ToOrderByString())));
        }

        if (queryBuilder.Limit is not null)
        {
            query.Append($" LIMIT {queryBuilder.Limit}");
        }

        if (queryBuilder.Offset is not null)
        {
            query.Append($" OFFSET {queryBuilder.Offset}");
        }

        return query.ToString();
    }

    /// <summary>
    /// Compiles a downsampling query as aggregate-then-join (AB#4713): an inner subquery reduces
    /// the filtered source rows per DATE_BIN bucket in a single scan, and generate_series is then
    /// LEFT JOINed onto the pre-aggregated result with a plain equi-join to materialize empty bins.
    /// The former shape joined generate_series directly against the source table ON
    /// <c>DATE_BIN(...) = bins.ts</c> — a function-expression join CrateDB cannot hash, so it
    /// degenerated to a nested loop of cost O(bins × rows) and month-range chart queries blew the
    /// 30 s per-attempt timeout ("Job killed"). The subquery's COUNT(time axis) AS "__binCount"
    /// detects empty bins; the outer COALESCE keeps it 0 (not NULL) for bins with no source row.
    /// </summary>
    private static string CompileDownsamplingQuery(CrateQueryBuilder queryBuilder)
    {
        var query = new StringBuilder();

        var interval = queryBuilder.To!.Value - queryBuilder.From!.Value;
        // Round (not truncate) to the nearest second and never go below 1s. Integer truncation
        // (e.g. 70.7 -> 70 for 1222 bins over a day) shrinks the bin below the source resolution
        // and, combined with the windowed containment predicate below, can drop every source row.
        // The caller clamps Limit to the source's distinct bucket count so the bin is never finer
        // than the data; this rounding keeps the bin aligned to that clamp.
        var intervalSeconds = Math.Max(1,
            (int)Math.Round(interval.TotalSeconds / queryBuilder.Limit!.Value, MidpointRounding.AwayFromZero));
        var intervalLiteral = $"'{intervalSeconds} seconds'::INTERVAL";
        // Normalise to UTC before formatting — Constants.DateTimeFormat appends a literal `Z`
        // suffix, so a Kind=Local DateTime would otherwise have its local-time digits stamped
        // with `Z` and end up off by the local offset on the CrateDB side. See the WHERE-clause
        // branch lower down for the full rationale.
        var fromUtc = queryBuilder.From.Value.ToUniversalTime();
        var fromLiteral = $"'{fromUtc.ToString(Constants.DateTimeFormat)}'::TIMESTAMP";
        // Compute exclusive upper bound: From + (Limit - 1) * interval
        // generate_series is inclusive on both ends, so we use Limit-1 intervals to get exactly Limit bins
        var seriesEnd = fromUtc.AddSeconds(intervalSeconds * (queryBuilder.Limit!.Value - 1));
        var seriesEndLiteral = $"'{seriesEnd.ToString(Constants.DateTimeFormat)}'::TIMESTAMP";

        // Windowed-storage downsampling: source is a rollup or time-range archive whose time
        // axis is `(window_start, window_end)`. Concept-time-range §7: a source window
        // contributes to a bin only when it is fully contained; straddling windows are dropped
        // from the target rather than pro-rated.
        var isWindowed = queryBuilder.TimeColumn == Constants.WindowEnd;
        var timeColumn = queryBuilder.TimeColumn;
        // Bin-membership column. For windowed archives a source row's bin is the one that
        // CONTAINS the window, identified by its window_start. Keying DATE_BIN on window_end
        // (the time axis) assigns a boundary-aligned window to the NEXT bin, so the
        // fully-contained predicate then always fails and the bin reads empty — the all-null
        // bug (AB#4246). window_start keeps the §7 containment semantic intact.
        var binColumn = isWindowed ? Constants.WindowStart : timeColumn;
        var binExpression = $"DATE_BIN({intervalLiteral}, d.\"{binColumn}\", {fromLiteral})";

        // ---- Outer SELECT: bin axis + pass-through of the pre-aggregated columns ----
        query.Append("SELECT bins.ts AS \"T\"");

        // Per-series group columns (e.g. src."rtid") carry the series identity out of the
        // subquery; they extend ORDER BY below (no outer GROUP BY needed — the subquery is
        // already unique per (bin, series)).
        foreach (var groupColumn in queryBuilder.DownsamplingGroupByColumns)
        {
            query.Append($", src.\"{groupColumn}\" AS \"{groupColumn}\"");
        }

        foreach (var variable in queryBuilder.QueryVariablesWithoutTimestamp)
        {
            var alias = variable.Alias ?? variable.Name;
            query.Append($", src.\"{alias}\" AS \"{alias}\"");
        }

        // Empty bins have no subquery row, so src."__binCount" reads NULL — COALESCE keeps the
        // 0-means-empty contract the row mapper relies on.
        query.Append(", COALESCE(src.\"__binCount\", 0) AS \"__binCount\"");

        // FROM generate_series(from, seriesEnd, interval) — exactly Limit bins
        query.Append($" FROM generate_series({fromLiteral}, {seriesEndLiteral}, {intervalLiteral}) AS bins(ts)");

        // ---- Inner subquery: single scan over the filtered source rows, reduced per bin ----
        query.Append(" LEFT JOIN (");
        query.Append($"SELECT {binExpression} AS bin_ts");

        foreach (var groupColumn in queryBuilder.DownsamplingGroupByColumns)
        {
            query.Append($", d.\"{groupColumn}\" AS \"{groupColumn}\"");
        }

        foreach (var variable in queryBuilder.QueryVariablesWithoutTimestamp)
        {
            query.Append(", ");
            if (variable.AggregationFunction != null || variable.IsRawExpression)
            {
                // Both classical aggregations (`AVG("col")`) and raw chain-aware expressions
                // (`SUM("col_sum") / NULLIF(SUM("col_count"), 0)`) embed column references the
                // same way: `<func>("<col>")`. The `(\"` → `(d.\"` rewrite pins every column to
                // the table alias `d` without having to teach the resolver about the prefix.
                var selectStr = variable.ToSelectString();
                query.Append(selectStr.Replace("(\"", "(d.\""));
            }
            else
            {
                query.Append($"d.{variable.ToSelectString()}");
            }
        }

        query.Append($", COUNT(d.\"{timeColumn}\") AS \"__binCount\"");

        query.Append($" FROM {queryBuilder.TenantId} AS d");
        query.Append(" WHERE 1 = 1");

        // Fully-contained predicate (concept-time-range §7). The former half
        // `window_start >= bins.ts` is implied here: DATE_BIN keys on window_start, and a bin
        // start is by definition <= the value it was derived from. Only the upper half remains.
        if (isWindowed)
        {
            query.Append($" AND d.\"{Constants.WindowEnd}\" <= {binExpression} + {intervalLiteral}");
        }

        // Source-row filter conditions (ckType, time range, IN-lists, field filters, generation).
        AppendDownsamplingSourceFilters(query, queryBuilder, isWindowed, timeColumn);

        query.Append($" GROUP BY {binExpression}");
        foreach (var groupColumn in queryBuilder.DownsamplingGroupByColumns)
        {
            query.Append($", d.\"{groupColumn}\"");
        }

        // Equi-join on the pre-computed bin timestamp — hash-joinable, unlike the former
        // function-expression ON clause.
        query.Append(") AS src ON src.bin_ts = bins.ts");

        query.Append(" ORDER BY bins.ts ASC");
        foreach (var groupColumn in queryBuilder.DownsamplingGroupByColumns)
        {
            query.Append($", src.\"{groupColumn}\" ASC");
        }

        return query.ToString();
    }

    /// <summary>
    /// Compiles a scalar query that returns the number of distinct source bins in the requested
    /// range under the same source filters the downsampling query uses (ckType, time range, rtId /
    /// field filters). The caller clamps the requested bucket <c>Limit</c> to this count so the
    /// generated bin can never be finer than the data — which, for windowed archives, would make
    /// the fully-contained predicate drop every source window (the all-null bug). The distinct
    /// column is the bin column: <c>window_start</c> for windowed archives, the time axis otherwise.
    /// </summary>
    public string CompileDownsamplingBucketCountQuery(CrateQueryBuilder queryBuilder)
    {
        var isWindowed = queryBuilder.TimeColumn == Constants.WindowEnd;
        var binColumn = isWindowed ? Constants.WindowStart : queryBuilder.TimeColumn;

        var query = new StringBuilder();
        query.Append($"SELECT COUNT(DISTINCT d.\"{binColumn}\") AS \"c\"");
        query.Append($" FROM {queryBuilder.TenantId} AS d");
        query.Append(" WHERE 1 = 1");
        AppendDownsamplingSourceFilters(query, queryBuilder, isWindowed, queryBuilder.TimeColumn);
        return query.ToString();
    }

    /// <summary>
    /// Appends the downsampling source-row filter conditions (each prefixed with <c> AND </c>,
    /// referencing the aliased table <c>d</c>): ckType, time-range overlap, IN-lists, field
    /// filters and the active-generation predicate. Shared by
    /// <see cref="CompileDownsamplingQuery"/> (subquery WHERE clause) and
    /// <see cref="CompileDownsamplingBucketCountQuery"/> (WHERE clause) so the two can never drift.
    /// </summary>
    private static void AppendDownsamplingSourceFilters(StringBuilder query, CrateQueryBuilder queryBuilder,
        bool isWindowed, string timeColumn)
    {
        if (queryBuilder.CkTypeId != null)
        {
            query.Append($" AND d.\"{Constants.CkTypeId}\" = '{queryBuilder.CkTypeId.SemanticVersionedFullName}'");
        }

        // Downsampling needs a closed range to derive the bin width, so both boundaries are
        // validated by ExecuteDownsamplingQueryAsync before we get here — unlike the general
        // WHERE-clause path, this one never has to emit a one-sided predicate.
        if (queryBuilder is { From: not null, To: not null })
        {
            // UTC-normalised — see the comment in the WHERE-clause section for why.
            var fromIso = queryBuilder.From.Value.ToUniversalTime().ToString(Constants.DateTimeFormat);
            var toIso = queryBuilder.To.Value.ToUniversalTime().ToString(Constants.DateTimeFormat);
            if (isWindowed)
            {
                // Windowed-storage downsampling source filter: bucket overlaps the range —
                // window_start < To AND window_end > From. Captures any source bucket whose
                // [start, end) interval intersects the requested time range, including buckets
                // that end exactly at To or start exactly at From.
                query.Append($" AND d.\"{Constants.WindowStart}\" < '{toIso}'");
                query.Append($" AND d.\"{Constants.WindowEnd}\" > '{fromIso}'");
            }
            else
            {
                query.Append($" AND d.\"{timeColumn}\" >= '{fromIso}'");
                query.Append($" AND d.\"{timeColumn}\" <= '{toIso}'");
            }
        }

        if (queryBuilder.VariableInListVariables.Any())
        {
            foreach (var variable in queryBuilder.VariableInListVariables)
            {
                query.Append($" AND d.{variable.ToVariableInListString()}");
            }
        }

        if (queryBuilder.HasFieldFilters)
        {
            foreach (var filter in queryBuilder.FieldFilters)
            {
                query.Append($" AND d.{CompileFieldFilter(filter)}");
            }
        }

        // Phase 6 (AB#4184): per-window active-generation filter for rollup archives. Was
        // previously only emitted on the general WHERE-clause path, so a downsampling read during
        // a recompute saw BOTH generations of the swapped windows and double-counted. The filter
        // references its columns unqualified — inside the aggregate-then-join subquery (and the
        // bucket-count probe) the single table alias `d` is the only column scope, so unqualified
        // names resolve to it.
        if (queryBuilder.GenerationTracked)
        {
            query.Append(" AND ");
            query.Append(CompileGenerationFilter(queryBuilder));
        }
    }

    /// <summary>
    /// Compiles a COUNT query using the same WHERE clause as CompileQuery, without SELECT columns, GROUP BY, ORDER BY, LIMIT, or OFFSET.
    /// </summary>
    public string CompileCountQuery(CrateQueryBuilder queryBuilder)
    {
        var query = new StringBuilder();
        query.Append($"SELECT COUNT(*) FROM {queryBuilder.TenantId}");
        AppendWhereClause(query, queryBuilder);
        return query.ToString();
    }

    private static void AppendWhereClause(StringBuilder query, CrateQueryBuilder queryBuilder)
    {
        if (queryBuilder.VariableInListVariables.Any() || queryBuilder.HasTimeFilter || queryBuilder.CkTypeId != null || queryBuilder.HasFieldFilters || queryBuilder.GenerationTracked)
        {
            // we can only have one where clause, but we can connect it with AND
            query.Append(" WHERE ");
        }

        if(queryBuilder.CkTypeId != null)
        {
            query.Append($"\"{Constants.CkTypeId}\" = '{queryBuilder.CkTypeId.SemanticVersionedFullName}'");

            if (queryBuilder.VariableInListVariables.Any() || queryBuilder.HasTimeFilter || queryBuilder.HasFieldFilters)
            {
                query.Append(" AND ");
            }
        }

        if (queryBuilder.VariableInListVariables.Any())
        {
            query.Append(string.Join(" AND ",
                queryBuilder.VariableInListVariables.Select(x => x.ToVariableInListString())));

            if (queryBuilder.HasTimeFilter || queryBuilder.HasFieldFilters)
            {
                // if we have a time filter as well, we have to connect the filter conditions with an AND
                query.Append(" AND ");
            }
        }

        if (queryBuilder.HasTimeFilter)
        {
            // Use the QueryBuilder's TimeColumn — defaults to `timestamp` for raw archives,
            // becomes `window_end` for windowed-storage archives (rollup / time-range) so the
            // WHERE clause references a column that actually exists on the per-archive table.
            // Normalise to UTC before formatting: Constants.DateTimeFormat has a literal `Z`
            // suffix, so CrateDB will read the rendered string as UTC. Without ToUniversalTime()
            // a DateTime whose Kind is Local (or Unspecified that defaulted to Local) would have
            // its local-time digits stamped with `Z`, putting the filter off by the local offset.
            // Convert.ToDateTime parses ISO `…Z` strings into Kind=Local on read, so persisted
            // SD-queries hitting this path are the typical victim — the GraphQL input arrives
            // as Kind=Utc but the value round-trips through Mongo `_attributes` and comes back
            // as Local. ToUniversalTime() is a no-op for Kind=Utc values.
            //
            // Each boundary is emitted independently so a one-sided range (only From or only To)
            // still filters instead of degrading to "no time predicate at all" (AB#4617).
            var fromIso = queryBuilder.From?.ToUniversalTime().ToString(Constants.DateTimeFormat);
            var toIso = queryBuilder.To?.ToUniversalTime().ToString(Constants.DateTimeFormat);
            if (queryBuilder.TimeColumn == Constants.WindowEnd)
            {
                // Windowed-storage time filter: bucket overlaps the requested range —
                // `window_start < to AND window_end > from`. Using the natural single-column
                // semantic (`window_end IN [from, to]`) would exclude any bucket whose end
                // falls exactly on or after `to` even though its body overlaps the range
                // (e.g. a Monthly bucket [2026-01-01, 2026-02-01) with the operator's filter
                // [2026-01-01, 2026-01-31] would be dropped). Overlap mirrors how operators
                // think about time ranges over bucketed data. With one boundary open, only the
                // half of the overlap test that the boundary constrains remains.
                query.Append(string.Join(" AND ", new[]
                {
                    toIso is null ? null : $"\"{Constants.WindowStart}\" < '{toIso}'",
                    fromIso is null ? null : $"\"{Constants.WindowEnd}\" > '{fromIso}'"
                }.Where(predicate => predicate is not null)));
            }
            else
            {
                query.Append(string.Join(" AND ", new[]
                {
                    fromIso is null ? null : $"\"{queryBuilder.TimeColumn}\" >= '{fromIso}'",
                    toIso is null ? null : $"\"{queryBuilder.TimeColumn}\" <= '{toIso}'"
                }.Where(predicate => predicate is not null)));
            }

            if (queryBuilder.HasFieldFilters)
            {
                query.Append(" AND ");
            }
        }

        if (queryBuilder.HasFieldFilters)
        {
            query.Append(string.Join(" AND ",
                queryBuilder.FieldFilters.Select(CompileFieldFilter)));
        }

        // Phase 6 (AB#4184): per-window active-generation filter for rollup archives. Emitted last,
        // self-managing its leading AND, so it composes with any combination of the conditions above
        // without touching their inter-condition AND handling.
        if (queryBuilder.GenerationTracked)
        {
            var anyPrior = queryBuilder.CkTypeId != null
                           || queryBuilder.VariableInListVariables.Any()
                           || queryBuilder.HasTimeFilter
                           || queryBuilder.HasFieldFilters;
            if (anyPrior)
            {
                query.Append(" AND ");
            }

            query.Append(CompileGenerationFilter(queryBuilder));
        }
    }

    /// <summary>
    /// Builds <c>"generation" = CASE WHEN &lt;range&gt; THEN &lt;gen&gt; … ELSE 0 END</c> from the active-
    /// generation ranges. Ranges are emitted newest-generation-first so an overlapping re-recompute
    /// (higher generation) wins via CASE's first-match semantics; windows in no recomputed range fall
    /// through to the steady-state generation 0. With no ranges (steady state, or a recompute that has
    /// staged its next generation but not yet flipped the pointer) the predicate collapses to the
    /// baseline <c>generation = 0</c> — never a CASE with no WHEN, which CrateDB would reject — so the
    /// not-yet-committed rows stay hidden. AB#4184, Phase 6.
    /// </summary>
    private static string CompileGenerationFilter(CrateQueryBuilder queryBuilder)
    {
        if (queryBuilder.GenerationRanges.Count == 0)
        {
            return $"\"{Constants.Generation}\" = 0";
        }

        var sb = new StringBuilder();
        sb.Append('"').Append(Constants.Generation).Append("\" = CASE");
        foreach (var range in queryBuilder.GenerationRanges.OrderByDescending(r => r.Generation))
        {
            sb.Append(" WHEN (\"").Append(Constants.WindowStart).Append("\" >= ").Append(range.StartMs)
              .Append(" AND \"").Append(Constants.WindowStart).Append("\" < ").Append(range.EndMs).Append(')');
            if (!string.IsNullOrEmpty(range.Scope))
            {
                sb.Append(" AND \"").Append(Constants.RtId).Append("\" = '").Append(range.Scope.Replace("'", "''")).Append('\'');
            }
            sb.Append(" THEN ").Append(range.Generation);
        }
        sb.Append(" ELSE 0 END");
        return sb.ToString();
    }

    /// <summary>
    /// Renders one field-filter predicate (without leading AND / table alias). Internal so the
    /// raw time-weighted query path (AB#4336 §6.2) reuses the exact same operator rendering.
    /// </summary>
    internal static string CompileFieldFilter(StreamDataFieldFilterDto filter)
    {
        // Direct camelCase column reference — the legacy `data['x']` indirection is gone.
        var fieldRef = $"\"{filter.FieldName}\"";

        switch (filter.Operator)
        {
            case StreamDataFieldFilterOperator.IsNull:
                return $"{fieldRef} IS NULL";

            case StreamDataFieldFilterOperator.IsNotNull:
                return $"{fieldRef} IS NOT NULL";

            case StreamDataFieldFilterOperator.Between:
                return $"{fieldRef} BETWEEN '{filter.Value}' AND '{filter.SecondaryValue}'";

            case StreamDataFieldFilterOperator.In:
            {
                var values = string.Join(", ", (filter.ValueList ?? []).Select(v => $"'{v}'"));
                return $"{fieldRef} IN ({values})";
            }

            case StreamDataFieldFilterOperator.NotIn:
            {
                var values = string.Join(", ", (filter.ValueList ?? []).Select(v => $"'{v}'"));
                return $"{fieldRef} NOT IN ({values})";
            }

            default:
            {
                var op = filter.Operator switch
                {
                    StreamDataFieldFilterOperator.Equals => "=",
                    StreamDataFieldFilterOperator.NotEquals => "!=",
                    StreamDataFieldFilterOperator.GreaterThan => ">",
                    StreamDataFieldFilterOperator.GreaterThanOrEqual => ">=",
                    StreamDataFieldFilterOperator.LessThan => "<",
                    StreamDataFieldFilterOperator.LessThanOrEqual => "<=",
                    StreamDataFieldFilterOperator.Like => "LIKE",
                    _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unsupported field filter operator")
                };
                return $"{fieldRef} {op} '{filter.Value}'";
            }
        }
    }
}