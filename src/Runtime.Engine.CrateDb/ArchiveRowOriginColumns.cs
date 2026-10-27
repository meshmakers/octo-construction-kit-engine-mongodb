using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// The platform columns that record a row's <see cref="ArchiveRowOrigin" /> (AB#5356) on every raw and
/// time-range archive table, and the SQL around them.
/// </summary>
/// <remarks>
/// <para>
/// Rollup tables do not carry them: their rows are derived by the platform, never written by a person.
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
    };

    /// <summary>Physical names of <see cref="Columns" />.</summary>
    public static IReadOnlyList<string> Names { get; } = Columns.Select(c => c.Name).ToArray();

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
