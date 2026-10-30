using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// The platform columns that record a row's <see cref="ArchiveRowOrigin" /> (AB#5356) and, for a manual
/// row, who wrote it, when and why (AB#7593) on every raw and time-range archive table, and the SQL around
/// them. An automated row carries NULL in the three manual columns.
/// </summary>
/// <remarks>
/// <para>
/// Rollup tables do not carry them: their rows are derived by the platform, never written by a person. A
/// rollup row reports the origin of what it aggregates in its own platform columns instead,
/// <see cref="RollupColumns" /> (AB#7727).
/// </para>
/// <para>
/// Every column is nullable without a DEFAULT, for two reasons. Tables provisioned before AB#5356 get the
/// columns added through <c>ALTER TABLE … ADD COLUMN</c>, which cannot add a NOT NULL column to a table
/// that holds rows; and a deterministic DEFAULT is not reliable in a multi-row upsert (AB#6647), so the
/// insert always names the column. A stored <c>NULL</c> origin is a row written before the origin was
/// recorded and reads as automated.
/// </para>
/// <para>
/// The names carry an underscore, which <see cref="ColumnNameMapper" /> never produces for a dotted
/// attribute path, so they do not collide with a user column.
/// </para>
/// </remarks>
internal static class ArchiveRowOriginColumns
{
    /// <summary>Stored value of <see cref="ArchiveRowOrigin.Automated" />.</summary>
    public const string AutomatedValue = "automated";

    /// <summary>Stored value of <see cref="ArchiveRowOrigin.Manual" />.</summary>
    public const string ManualValue = "manual";

    /// <summary>The platform columns with their DDL type, in the order the insert binds them.</summary>
    public static IReadOnlyList<(string Name, string Definition)> Columns { get; } = new (string, string)[]
    {
        (Constants.RowOrigin, "TEXT"),
        (Constants.ManualWrittenBy, "TEXT"),
        (Constants.ManualWrittenAt, "TIMESTAMP WITH TIME ZONE"),
        (Constants.ManualReason, "TEXT"),
    };

    /// <summary>Physical names of <see cref="Columns" />.</summary>
    public static IReadOnlyList<string> Names { get; } = Columns.Select(c => c.Name).ToArray();

    /// <summary>
    /// The platform columns of every rollup table with their DDL type (AB#7727): how many manually entered
    /// source values a bucket holds and when the latest of them was written. Nullable without a DEFAULT and
    /// always named in the aggregation INSERT, for the reasons above. Existing rollup rows hold <c>NULL</c>
    /// after the <c>ALTER TABLE … ADD COLUMN</c>, which means "no manual values".
    /// </summary>
    public static IReadOnlyList<(string Name, string Definition)> RollupColumns { get; } = new (string, string)[]
    {
        (Constants.ManualCount, "BIGINT"),
        (Constants.LastManualAt, "TIMESTAMP WITH TIME ZONE"),
    };

    /// <summary>
    /// The SQL that reads <paramref name="column" /> on the read path (AB#7616). Every column is a plain quoted
    /// identifier, except the two whose stored <c>NULL</c> means something (an <c>ALTER TABLE … ADD COLUMN</c>
    /// does not fill existing rows, not even with a DEFAULT): <c>row_origin</c> of a row written before the
    /// origin was recorded reads as automated, <c>manual_count</c> of a rollup row aggregated before the
    /// column existed reads as 0 (AB#7727) — in projections, filters, grouping and sorting alike.
    /// </summary>
    /// <param name="column">Physical column name.</param>
    /// <param name="tableAlias">Optional table alias the column is qualified with.</param>
    public static string ColumnRef(string column, string? tableAlias = null)
    {
        var qualified = tableAlias is null ? $"\"{column}\"" : $"{tableAlias}.\"{column}\"";
        return column switch
        {
            Constants.RowOrigin => $"COALESCE({qualified}, '{AutomatedValue}')",
            Constants.ManualCount => $"COALESCE({qualified}, 0)",
            _ => qualified
        };
    }

    /// <summary>True when <see cref="ColumnRef" /> reads the column through an expression, which needs an alias.</summary>
    public static bool IsReadAsExpression(string column) => column is Constants.RowOrigin or Constants.ManualCount;

    /// <summary>
    /// <see cref="ColumnRef" /> as a SELECT item that keeps the column name as its output name.
    /// </summary>
    public static string SelectRef(string column, string? tableAlias = null)
    {
        var reference = ColumnRef(column, tableAlias);
        return IsReadAsExpression(column) ? $"{reference} AS \"{column}\"" : reference;
    }

    /// <summary>The stored text of an origin.</summary>
    public static string ToStoredValue(ArchiveRowOrigin origin)
        => origin == ArchiveRowOrigin.Manual ? ManualValue : AutomatedValue;

    /// <summary>
    /// The origin of a stored value: <c>manual</c> is manual, anything else (also <c>NULL</c>, a row
    /// written before the origin was recorded) is automated.
    /// </summary>
    public static ArchiveRowOrigin FromStoredValue(object? value)
        => value is string s && string.Equals(s, ManualValue, StringComparison.OrdinalIgnoreCase)
            ? ArchiveRowOrigin.Manual
            : ArchiveRowOrigin.Automated;

    /// <summary>The incoming write is manual (reads the <c>EXCLUDED</c> row of an upsert).</summary>
    public static string IncomingIsManual => $"EXCLUDED.\"{Constants.RowOrigin}\" = '{ManualValue}'";

    /// <summary>The stored row is manual; a stored <c>NULL</c> is automated.</summary>
    public static string StoredIsManual => $"COALESCE(\"{Constants.RowOrigin}\", '{AutomatedValue}') = '{ManualValue}'";
}
