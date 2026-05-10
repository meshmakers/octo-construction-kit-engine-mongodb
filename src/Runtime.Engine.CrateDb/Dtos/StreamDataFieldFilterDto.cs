namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

/// <summary>
/// Operators for stream data field filters
/// </summary>
public enum StreamDataFieldFilterOperator
{
    /// <summary>Equality check (=)</summary>
    Equals,

    /// <summary>Inequality check (!=)</summary>
    NotEquals,

    /// <summary>Greater than (&gt;)</summary>
    GreaterThan,

    /// <summary>Greater than or equal (&gt;=)</summary>
    GreaterThanOrEqual,

    /// <summary>Less than (&lt;)</summary>
    LessThan,

    /// <summary>Less than or equal (&lt;=)</summary>
    LessThanOrEqual,

    /// <summary>SQL LIKE pattern match</summary>
    Like,

    /// <summary>SQL IN list check</summary>
    In,

    /// <summary>SQL NOT IN list check</summary>
    NotIn,

    /// <summary>SQL IS NULL check</summary>
    IsNull,

    /// <summary>SQL IS NOT NULL check</summary>
    IsNotNull,

    /// <summary>SQL BETWEEN range check (inclusive); requires SecondaryValue</summary>
    Between
}

/// <summary>
/// A single field filter condition for stream data queries
/// </summary>
/// <param name="FieldName">CamelCase column name on the per-archive table.</param>
/// <param name="Operator">Comparison operator</param>
/// <param name="Value">Primary comparison value</param>
/// <param name="SecondaryValue">Upper bound for Between operator</param>
/// <param name="ValueList">List of values for In/NotIn operators</param>
public record StreamDataFieldFilterDto(
    string FieldName,
    StreamDataFieldFilterOperator Operator,
    string Value,
    string? SecondaryValue = null,
    IReadOnlyList<string>? ValueList = null);
