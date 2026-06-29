using System.Globalization;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Shared CrateDB SQL literal formatting for the computed-column write paths
/// (<see cref="RollupComputedColumnSqlBuilder"/>, <see cref="ComputedColumnBackfillSqlBuilder"/>).
/// Computed values are finite numbers / booleans / timestamps / NULL — the formula engine already
/// maps NaN and the null sentinel to NULL — and key values are timestamps / strings, so inline
/// literals are safe (string literals are single-quote-escaped defensively).
/// </summary>
internal static class CrateSqlLiteral
{
    /// <summary>A millisecond-precision <c>timestamp with time zone</c> literal in UTC.</summary>
    public static string Timestamp(System.DateTime dt) =>
        $"'{dt.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}'::timestamp with time zone";

    /// <summary>A single-quote-escaped string literal.</summary>
    public static string String(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Formats a CLR value as a CrateDB literal. Non-finite reals collapse to <c>NULL</c> (the
    /// formula engine already mapped NaN / the null sentinel to NULL, but this is defensive).
    /// </summary>
    public static string Value(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "NULL",
        float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "NULL",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        System.DateTime dt => Timestamp(dt),
        _ => String(value.ToString() ?? string.Empty),
    };
}
