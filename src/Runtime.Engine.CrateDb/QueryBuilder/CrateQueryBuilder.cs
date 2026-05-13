using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// Query Builder
/// </summary>
internal class CrateQueryBuilder
{
    /// <summary>
    /// All Variables in the query
    /// </summary>
    internal List<IQueryVariable> Variables { get; } = new();
    
    /// <summary>
    /// Physical column name to filter / sort / DATE_BIN against for the time axis. Defaults to
    /// the raw-archive <c>timestamp</c> column; <see cref="UseWindowedTimeAxis"/> switches to the
    /// <c>window_end</c> column for rollup / time-range archives that store half-open
    /// <c>[window_start, window_end)</c> rows instead of point-in-time measurements.
    /// </summary>
    internal string TimeColumn { get; private set; } = Constants.Timestamp;

    internal IEnumerable<IQueryVariable> QueryVariablesWithoutTimestamp =>
        Variables.Where(x => x.Name != Constants.Timestamp && x.Alias != Constants.Timestamp);

    /// <summary>
    /// All variables to ordered by
    /// </summary>
    internal List<IQueryVariable> OrderByVariables { get; } = new();

    /// <summary>
    /// Time Stamp Variable
    /// </summary>
    /// <summary>
    /// Finds the variable that represents the result-set's logical <c>timestamp</c> column. For
    /// raw archives this is the actual <c>timestamp</c> column; for windowed-storage archives
    /// the variable is aliased as <c>timestamp</c> over the physical <c>window_end</c> column,
    /// so the lookup matches either the name or the alias.
    /// </summary>
    internal IQueryVariable? TimeStampVariable =>
        Variables.FirstOrDefault(x => x.Name == Constants.Timestamp || x.Alias == Constants.Timestamp);

    /// <summary>
    /// Schema-qualified, double-quoted legacy stream-data table identifier for this tenant
    /// (e.g. <c>"acmecorp"."streamData"</c>). Embedded directly into <c>FROM</c> and
    /// <c>LEFT JOIN</c> clauses by <see cref="CrateQueryCompiler"/>.
    /// </summary>
    internal string TenantId { get; }

    /// <summary>
    /// Time Filter
    /// </summary>
    internal DateTime? From { get; private set; }

    /// <summary>
    /// Time Filter
    /// </summary>
    internal DateTime? To { get; private set; }

    /// <summary>
    /// True when the variable list contains at least one aggregation — either a classical one
    /// (<see cref="QueryVariable.AggregationFunction"/> set) or a raw-expression variable from
    /// the chain-aware rollup path which already carries the aggregation inside its SQL fragment
    /// (e.g. <c>SUM(_sum) / NULLIF(SUM(_count), 0)</c>). Drives the downsampling SELECT-shape
    /// branch in the compiler — without recognising raw expressions a downsampling query with
    /// only chain-aware aggregations would silently fall through to the non-aggregating path.
    /// </summary>
    internal bool HasAggregations => Variables.Any(x => x.AggregationFunction != null || x.IsRawExpression);
    
    /// <summary>
    /// 
    /// </summary>
    internal bool HasOrderBy => OrderByVariables.Count > 0;

    /// <summary>
    /// Variables to be included in the group by clause. Excludes both classical aggregation
    /// variables (those carry <see cref="IQueryVariable.AggregationFunction"/>) and raw-expression
    /// variables — the latter are SELECT-clause SQL fragments built by the chain-aware rollup
    /// resolver and contain aggregate calls like <c>SUM("voltage_avg_sum")</c>; putting them
    /// into GROUP BY would have CrateDB reject the query with "Aggregate functions are not
    /// allowed in GROUP BY".
    /// </summary>
    internal IEnumerable<IQueryVariable> Groupings =>
        Variables.Where(x => x.AggregationFunction == null && !x.IsRawExpression);

    internal IEnumerable<IQueryVariable> VariableInListVariables => Variables.Where(x => x.HasVariableInListVariables);

    internal List<StreamDataFieldFilterDto> FieldFilters { get; } = new();

    internal bool HasFieldFilters => FieldFilters.Count > 0;

    internal int? Limit { get; private set; }
    
    internal int? Offset { get; private set; }

    internal RtCkId<CkTypeId>? CkTypeId { get; private set; }

    /// <summary>
    /// Constructor. <paramref name="qualifiedTable"/> is the schema-qualified, double-quoted
    /// archive table identifier (e.g. <c>"acmecorp"."archive_65d5..."</c>) — typically obtained
    /// via <see cref="TenantSchema.QualifiedArchiveTable"/>. After the T17 hard cut every query
    /// targets a per-archive table; the legacy single-tenant table is gone.
    /// </summary>
    public CrateQueryBuilder(string qualifiedTable)
    {
        TenantId = qualifiedTable;
    }

    /// <summary>
    /// Adds a time filter to the query
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public CrateQueryBuilder WithTimeFilter(DateTime from, DateTime to)
    {
        From = from;
        To = to;
        return this;
    }

    /// <summary>
    /// Adds a type filter to the query
    /// </summary>
    /// <param name="ckTypeId"></param>
    /// <returns></returns>
    public CrateQueryBuilder WithCkTypeIdFilter(RtCkId<CkTypeId> ckTypeId)
    {
        this.CkTypeId = ckTypeId;
        return this;
    }

    /// <summary>
    /// Adds a normal variable to the query. <paramref name="variableName"/> is taken verbatim as
    /// the camelCase column name on the per-archive table.
    /// </summary>
    public CrateQueryBuilder AddVariable(string variableName, string? alias = null, AggregationFunctionDto? aggregationFunction = null)
    {
        IQueryVariable variable = new QueryVariable(variableName, alias, aggregationFunction);

        var idx = Variables.FindIndex(x => x.Name == variable.Name || x.Alias == variable.Alias);
        if (idx != -1)
        {
            Variables[idx] = variable;
            return this;
        }
        Variables.Add(variable);
        return this;
    }

    /// <summary>
    /// Switches the query's time axis from the default <c>timestamp</c> column to the
    /// <c>window_end</c> column used by rollup / time-range archive tables. Must be called
    /// *before* <see cref="IncludeDefaultVariables"/> so the default-variable set picks up the
    /// windowed shape (window_end aliased as timestamp, plus window_start as an extra column).
    /// Concept-time-range §6 read-compatibility layer.
    /// </summary>
    public CrateQueryBuilder UseWindowedTimeAxis()
    {
        TimeColumn = Constants.WindowEnd;
        return this;
    }

    /// <summary>
    /// Includes all default variables that are available for every type. Branches on
    /// <see cref="TimeColumn"/>: for raw archives the canonical six (timestamp + identity +
    /// audit), for windowed archives <c>window_end</c> is aliased as <c>timestamp</c> so
    /// downstream consumers (StreamDataRow.Timestamp, MapToDataPointDto's Timestamp lookup)
    /// keep working with no code change, plus <c>window_start</c> as a separate extra column
    /// so callers that want the full window can request it explicitly.
    /// </summary>
    public CrateQueryBuilder IncludeDefaultVariables()
    {
        if (TimeColumn == Constants.WindowEnd)
        {
            // Logical "timestamp" surface for windowed storage. SQL output:
            //   "window_end" AS "timestamp", "window_start", "rtid", "cktypeid", ...
            Variables.Add(new QueryVariable(Constants.WindowEnd, Constants.Timestamp, null));
            Variables.Add(new QueryVariable(Constants.WindowStart, null, null));
            foreach (var f in new[] { Constants.RtId, Constants.CkTypeId, Constants.RtWellKnownName, Constants.RtCreationDateTime, Constants.RtChangedDateTime })
            {
                Variables.Add(new QueryVariable(f, null, null));
            }
            return this;
        }

        foreach(var streamDataField in Constants.DefaultStreamDataFields)
        {
            IQueryVariable variable = new QueryVariable(streamDataField, null, null);
            Variables.Add(variable);
        }

        return this;
    }


    /// <summary>
    /// Adds an aggregation variable to the query.
    /// </summary>
    public CrateQueryBuilder AddAggregationVariable(string name, AggregationFunctionDto aggregate, string? alias = null)
    {
        var variableAlias = alias ?? $"{aggregate.ToString()}_{name}";
        IQueryVariable variable = new QueryVariable(name, variableAlias, aggregate);
        Variables.Add(variable);
        return this;
    }

    /// <summary>
    /// Adds an aggregation column whose SELECT-clause SQL is a caller-provided raw expression.
    /// Used by the chain-aware rollup-query path which derives non-trivial expressions like
    /// <c>SUM("voltage_avg_sum") / NULLIF(SUM("voltage_avg_count"), 0)</c> from a target
    /// (path, function) pair. The expression is taken verbatim — callers must build it safely
    /// (no operator-supplied strings).
    /// </summary>
    /// <param name="rawExpression">The SQL fragment that goes into SELECT (without alias).</param>
    /// <param name="alias">Alias used for the column header + ORDER BY / GROUP BY referencing.</param>
    public CrateQueryBuilder AddRawAggregationExpression(string rawExpression, string alias)
    {
        Variables.Add(QueryVariable.RawExpression(rawExpression, alias));
        return this;
    }

    /// <summary>
    /// Adds a 
    /// </summary>
    /// <param name="nameOrAlias"></param>
    /// <param name="sortOrder"></param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder OrderBy(string nameOrAlias, SortOrderDto sortOrder)
    {
        var variable = Variables.FirstOrDefault(x=> x.Name == nameOrAlias || x.Alias == nameOrAlias);
        if (variable == null)
        {
            throw QueryBuilderException.OrderByVariableNotFound(nameOrAlias);
        }

        variable.SortOrder = sortOrder;

        OrderByVariables.Add(variable);
        return this;
    }

    /// <summary>
    /// Adds a tiebreaker column to the ORDER BY clause if there is already an ORDER BY
    /// and the specified variable is not yet in it. This ensures deterministic sort order
    /// for OFFSET-based pagination when the primary sort has many equal values (e.g., NULLs).
    /// </summary>
    /// <param name="nameOrAlias">The variable name or alias to use as tiebreaker</param>
    /// <param name="sortOrder">The sort direction for the tiebreaker</param>
    /// <returns></returns>
    public CrateQueryBuilder AddOrderByTiebreaker(string nameOrAlias, SortOrderDto sortOrder)
    {
        if (OrderByVariables.Count == 0)
        {
            return this;
        }

        if (OrderByVariables.Any(v => v.Name == nameOrAlias || v.Alias == nameOrAlias))
        {
            return this;
        }

        return OrderBy(nameOrAlias, sortOrder);
    }

    /// <summary>
    /// Add a list of values wher e
    /// </summary>
    /// <param name="nameOrAlias"></param>
    /// <param name="list"></param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder AddWhereIn(string nameOrAlias, string[] list)
    {
        var variable = Variables.FirstOrDefault(x => x.Name == nameOrAlias || x.Alias == nameOrAlias);
        if (variable == null)
        {
            throw QueryBuilderException.WhereInVariableNotFound(nameOrAlias);
        }

        variable.AddWhereInListItems(list);

        return this;
    }

    /// <summary>
    /// Adds a field filter condition to the WHERE clause
    /// </summary>
    /// <param name="fieldName">Column name</param>
    /// <param name="op">Comparison operator</param>
    /// <param name="value">Primary comparison value</param>
    /// <param name="secondaryValue">Upper bound value for the Between operator</param>
    /// <param name="valueList">List of values for In/NotIn operators</param>
    public CrateQueryBuilder AddFieldFilter(string fieldName, StreamDataFieldFilterOperator op, string value,
        string? secondaryValue = null, IReadOnlyList<string>? valueList = null)
    {
        FieldFilters.Add(new StreamDataFieldFilterDto(fieldName, op, value, secondaryValue, valueList));
        return this;
    }

    /// <summary>
    /// Sets a limit on the query
    /// </summary>
    /// <param name="limit">must be a positive integer (limit>0)</param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder WithLimit(int limit)
    {
        if (limit < 1)
        {
            throw QueryBuilderException.LimitMustBeGreaterThanZero();
        }
        Limit = limit;
        return this;
    }
    
    /// <summary>
    /// Sets an offset on the query
    /// </summary>
    /// <param name="offset">must be a positive integer including zero (offset>=0)</param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder WithOffset(int offset)
    {
        if (offset < 0)
        {
            throw QueryBuilderException.OffsetMustBeGreaterThanZero();
        }
        Offset = offset;
        return this;
    }

    /// <summary>
    /// Adds a downsampling to the query
    /// </summary>
    /// <param name="limit">Amount of points</param>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public CrateQueryBuilder WithDownsampling(int limit, DateTime from, DateTime to)
    {
        From = from;
        To = to;
        QueryMode = QueryModeDto.Downsampling;
        Limit = limit;
        return this;
    }

    /// <summary>
    /// Defines what kind of query should be executed
    /// </summary>
    public QueryModeDto? QueryMode { get; set; }
}