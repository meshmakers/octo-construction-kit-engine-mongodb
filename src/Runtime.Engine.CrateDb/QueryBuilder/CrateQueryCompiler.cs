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
    /// Compiles a downsampling query using generate_series LEFT JOIN to produce all time bins
    /// including empty ones. Adds COUNT(d."Timestamp") AS "__binCount" to detect empty bins
    /// (COUNT(*) would always return 1 due to LEFT JOIN producing a row for every bin).
    /// </summary>
    private static string CompileDownsamplingQuery(CrateQueryBuilder queryBuilder)
    {
        var query = new StringBuilder();

        var interval = queryBuilder.To!.Value - queryBuilder.From!.Value;
        var intervalSeconds = (int)interval.TotalSeconds / queryBuilder.Limit!.Value;
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
        // axis is `(window_start, window_end)`. The DATE_BIN expression and the bucket-membership
        // predicate target `window_end`; an extra fully-contained check (`window_start >= bin`)
        // drops source windows that straddle target bucket boundaries — concept-time-range §7
        // says straddling windows are dropped from the target rather than pro-rated.
        var isWindowed = queryBuilder.TimeColumn == Constants.WindowEnd;
        var timeColumn = queryBuilder.TimeColumn;

        // After T17 every attribute is a first-class typed column on the per-archive table — no
        // more `data['x']` indirection — so each variable becomes a qualified column reference.
        // SELECT bins.ts AS "T", AGG(d."voltage") AS "alias", COUNT(d."timestamp") AS "__binCount"
        query.Append("SELECT bins.ts AS \"T\"");

        // Per-series group columns (e.g. d."rtid") are selected verbatim so the result carries the
        // series identity; they are added to GROUP BY / ORDER BY below.
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
                // the joined table alias `d` without having to teach the resolver about the
                // join-side prefix.
                var selectStr = variable.ToSelectString();
                query.Append(selectStr.Replace("(\"", "(d.\""));
            }
            else
            {
                query.Append($"d.{variable.ToSelectString()}");
            }
        }

        query.Append($", COUNT(d.\"{timeColumn}\") AS \"__binCount\"");

        // FROM generate_series(from, seriesEnd, interval) — exactly Limit bins
        query.Append($" FROM generate_series({fromLiteral}, {seriesEndLiteral}, {intervalLiteral}) AS bins(ts)");

        // LEFT JOIN — bin membership keyed on the appropriate time column (timestamp for raw
        // archives, window_end for windowed archives).
        query.Append($" LEFT JOIN {queryBuilder.TenantId} AS d ON DATE_BIN({intervalLiteral}, d.\"{timeColumn}\", {fromLiteral}) = bins.ts");

        // Fully-contained predicate (concept-time-range §7): a windowed source row contributes
        // to bin B only when its entire [window_start, window_end) fits inside B. Without this,
        // a source window that crosses a bin boundary would land in whichever bin its
        // window_end falls into and silently double-count or misattribute values.
        if (isWindowed)
        {
            query.Append($" AND d.\"{Constants.WindowStart}\" >= bins.ts");
            query.Append($" AND d.\"{Constants.WindowEnd}\" <= bins.ts + {intervalLiteral}");
        }

        // All filter conditions go into the ON clause (not WHERE, since LEFT JOIN)
        if (queryBuilder.CkTypeId != null)
        {
            query.Append($" AND d.\"{Constants.CkTypeId}\" = '{queryBuilder.CkTypeId.SemanticVersionedFullName}'");
        }

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

        // GROUP BY and ORDER BY — no LIMIT needed since generate_series produces exactly Limit bins.
        // Per-series group columns extend both clauses so each series gets its own run of bins.
        query.Append(" GROUP BY bins.ts");
        foreach (var groupColumn in queryBuilder.DownsamplingGroupByColumns)
        {
            query.Append($", d.\"{groupColumn}\"");
        }

        query.Append(" ORDER BY bins.ts ASC");
        foreach (var groupColumn in queryBuilder.DownsamplingGroupByColumns)
        {
            query.Append($", d.\"{groupColumn}\" ASC");
        }

        return query.ToString();
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
        if (queryBuilder.VariableInListVariables.Any() || queryBuilder is { From: not null, To: not null } || queryBuilder.CkTypeId != null || queryBuilder.HasFieldFilters)
        {
            // we can only have one where clause, but we can connect it with AND
            query.Append(" WHERE ");
        }

        if(queryBuilder.CkTypeId != null)
        {
            query.Append($"\"{Constants.CkTypeId}\" = '{queryBuilder.CkTypeId.SemanticVersionedFullName}'");

            if (queryBuilder.VariableInListVariables.Any() || queryBuilder is { From: not null, To: not null } || queryBuilder.HasFieldFilters)
            {
                query.Append(" AND ");
            }
        }

        if (queryBuilder.VariableInListVariables.Any())
        {
            query.Append(string.Join(" AND ",
                queryBuilder.VariableInListVariables.Select(x => x.ToVariableInListString())));

            if (queryBuilder is { From: not null, To: not null } || queryBuilder.HasFieldFilters)
            {
                // if we have a time filter as well, we have to connect the filter conditions with an AND
                query.Append(" AND ");
            }
        }

        if (queryBuilder is { From: not null, To: not null })
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
            var fromUtc = queryBuilder.From.Value.ToUniversalTime();
            var toUtc = queryBuilder.To.Value.ToUniversalTime();
            var fromIso = fromUtc.ToString(Constants.DateTimeFormat);
            var toIso = toUtc.ToString(Constants.DateTimeFormat);
            if (queryBuilder.TimeColumn == Constants.WindowEnd)
            {
                // Windowed-storage time filter: bucket overlaps the requested range —
                // `window_start < to AND window_end > from`. Using the natural single-column
                // semantic (`window_end IN [from, to]`) would exclude any bucket whose end
                // falls exactly on or after `to` even though its body overlaps the range
                // (e.g. a Monthly bucket [2026-01-01, 2026-02-01) with the operator's filter
                // [2026-01-01, 2026-01-31] would be dropped). Overlap mirrors how operators
                // think about time ranges over bucketed data.
                query.Append(
                    $"\"{Constants.WindowStart}\" < '{toIso}' AND \"{Constants.WindowEnd}\" > '{fromIso}'");
            }
            else
            {
                query.Append(
                    $"\"{queryBuilder.TimeColumn}\" >= '{fromIso}' AND \"{queryBuilder.TimeColumn}\" <= '{toIso}'");
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
    }

    private static string CompileFieldFilter(StreamDataFieldFilterDto filter)
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