using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// Query Variable
/// </summary>
internal record QueryVariable : IQueryVariable
{
    private readonly List<string> _variableContainedInList = [];

    /// <summary>
    /// Query Variable. After the T17 hard cut every attribute is a first-class column on the
    /// per-archive table, so the legacy <c>data['x']</c> indirection is gone — <paramref name="name"/>
    /// is taken as the camelCase column name verbatim.
    /// </summary>
    public QueryVariable(string name,
        string? alias,
        AggregationFunctionDto? AggregationFunction)
    {
        this.AggregationFunction = AggregationFunction;
        Name = name;

        if (AggregationFunction != null)
        {
            Name = $"{AggregationFunction.ToString()!.ToUpper()}(\"{Name}\")";
        }

        Alias = alias ?? Name;
    }

    /// <summary>
    /// Factory for "raw expression" variables — the SELECT clause emits <paramref name="expression"/>
    /// verbatim with <paramref name="alias"/> as the column header. Used by the chain-aware
    /// rollup-query path which composes non-trivial aggregation SQL like
    /// <c>SUM("voltage_avg_sum") / NULLIF(SUM("voltage_avg_count"), 0)</c>. Callers must build
    /// the expression safely; the value is not escaped.
    /// </summary>
    public static QueryVariable RawExpression(string expression, string alias)
        => new(expression, alias, AggregationFunction: null) { IsRawExpression = true };

    /// <summary>
    /// When true, <see cref="ToSelectString"/> emits the <see cref="Name"/> verbatim (no quoting),
    /// aliased by <see cref="Alias"/>. The default false branch wraps the name in identifier
    /// quotes for safe single-column references.
    /// </summary>
    public bool IsRawExpression { get; init; }

    /// <summary>
    /// Adds items to the VariableIn collection
    /// </summary>
    /// <param name="items"></param>
    public void AddWhereInListItems(string[] items)
    {
        _variableContainedInList.AddRange(items);
    }

    public string ToVariableInListString()
    {
        var escapedValue = _variableContainedInList.Select(x => $"'{x}'");
        var list = string.Join(", ", escapedValue);
        return $"\"{Alias ?? Name}\" IN ({list})";
    }

    public bool HasVariableInListVariables => _variableContainedInList.Count > 0;
    
    /// <inheritdoc />
    public SortOrderDto? SortOrder { get; set; }

    /// <summary></summary>
    public string Name { get; init; }

    /// <summary></summary>
    public string? Alias { get; init; }

    /// <summary></summary>
    public AggregationFunctionDto? AggregationFunction { get; init; }

    /// <inheritdoc />

    public string ToSelectString()
    {
        if (IsRawExpression)
        {
            // Emit the SQL fragment verbatim — the caller built it (chain-aware rollup
            // aggregation, etc.) and we must not wrap it in identifier quotes.
            return Alias == Name ? Name : $"{Name} AS \"{Alias}\"";
        }
        if (AggregationFunction == null)
        {
            return Alias == Name ? $"\"{Name}\"" : $"\"{Name}\" AS \"{Alias}\"";
        }
        return Alias == Name ? Name : $"{Name} AS \"{Alias}\"";
    }

    /// <inheritdoc />
    public string ToGroupByString()
    {
        return $"\"{Alias}\"";
    }
    
    public string ToOrderByString()
    {
        return $"\"{Alias}\" {GetSortOrderString()}";
    }

    private string GetSortOrderString()
    {
        return SortOrder == SortOrderDto.Descending ? "DESC" : "ASC";
    }

    public void Deconstruct(out string name, out string? alias, out AggregationFunctionDto? aggregationFunction)
    {
        name = Name;
        alias = Alias;
        aggregationFunction = AggregationFunction;
    }
}