using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// A row of an insert batch that the conflict guard did not store (AB#7598): its position in the batch the
/// caller handed in, and why.
/// </summary>
/// <param name="Index">Zero-based position of the row in the batch passed to the insert.</param>
/// <param name="Reason">Why the stored row was kept.</param>
public sealed record SkippedArchiveRow(int Index, StreamDataSkipReason Reason);

/// <summary>
/// Tells which rows of a guarded upsert were stored (AB#7598).
/// </summary>
/// <remarks>
/// <para>
/// A guarded upsert (<c>ON CONFLICT … DO UPDATE SET col = CASE WHEN guard THEN EXCLUDED.col ELSE col END</c>)
/// always "updates" a conflicting row, also when the guard keeps the stored values, so the affected-row count
/// cannot tell a win from a loss. The statement therefore ends with <c>RETURNING</c> the row key, the ranking
/// columns and the origin, and a row counts as stored when what CrateDB holds afterwards equals what was sent
/// in those columns. That is exact: the guard's last key admits equality, so a write equal to the stored row in
/// every ranking column always wins, and a write that lost leaves a stored row that differs from it in at least
/// the deciding column — or in the origin, when a manual value was kept against an automated delivery.
/// </para>
/// <para>
/// Values are compared the way CrateDB stores them: timestamps at millisecond precision in UTC, numbers by value
/// whatever CLR type the driver or the writer used, text ordinally.
/// </para>
/// </remarks>
internal static class ConflictOutcome
{
    /// <summary>One row as it was sent: its key, the values of the ranking columns and its origin.</summary>
    internal sealed record SentRow(
        string? RtId, string? CkTypeId, DateTime Start, DateTime? End,
        IReadOnlyDictionary<string, object?> Values, ArchiveRowOrigin Origin);

    /// <summary>
    /// The <c>RETURNING</c> clause (leading space included) for a guarded insert: the row key, the physical ranking
    /// columns (the origin key is the <c>row_origin</c> column) and, when the table records it, the origin.
    /// </summary>
    public static string ReturningClause(bool windowed, IReadOnlyList<ArchiveConflictKey> precedence, bool includeOrigin)
    {
        var columns = new List<string> { Constants.RtId, Constants.CkTypeId };
        columns.AddRange(windowed ? [Constants.WindowStart, Constants.WindowEnd] : [Constants.Timestamp]);
        columns.AddRange(precedence.Where(k => !k.IsOrigin).Select(k => k.Column));
        if (includeOrigin)
        {
            columns.Add(Constants.RowOrigin);
        }

        return " RETURNING " + string.Join(", ", columns.Distinct(StringComparer.Ordinal).Select(c => $"\"{c}\""));
    }

    /// <summary>
    /// The rows of <paramref name="sent" /> whose values are not what CrateDB returned for their key, with the reason.
    /// A row for which CrateDB returned nothing is reported as not stored as well — it never happens for an upsert,
    /// and counting it as stored would be the success-without-write this exists to rule out.
    /// </summary>
    public static IReadOnlyList<SkippedArchiveRow> FindSkipped(
        IReadOnlyList<SentRow> sent,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> returned,
        IReadOnlyList<ArchiveConflictKey> precedence,
        bool includeOrigin,
        bool windowed)
    {
        var keyColumns = precedence.Where(k => !k.IsOrigin).Select(k => k.Column).Distinct(StringComparer.Ordinal).ToList();

        // Only a guard that ranks by origin protects a manual value; otherwise the ranking columns decided (AB#7656).
        var ranksByOrigin = precedence.Any(k => k.IsOrigin);

        // Returned rows by key; a batch may name one key twice, so each key holds a list that is consumed.
        var byKey = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var row in returned)
        {
            var key = Key(
                row.TryGetValue(Constants.RtId, out var rtId) ? rtId as string : null,
                row.TryGetValue(Constants.CkTypeId, out var ckTypeId) ? ckTypeId as string : null,
                AsUtcMilliseconds(row.TryGetValue(windowed ? Constants.WindowStart : Constants.Timestamp, out var start) ? start : null),
                windowed ? AsUtcMilliseconds(row.TryGetValue(Constants.WindowEnd, out var end) ? end : null) : null);
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = [];
            }

            list.Add(row);
        }

        var skipped = new List<SkippedArchiveRow>();
        for (var i = 0; i < sent.Count; i++)
        {
            var s = sent[i];
            var key = Key(s.RtId, s.CkTypeId, AsUtcMilliseconds(s.Start), windowed ? AsUtcMilliseconds(s.End) : null);
            if (!byKey.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                skipped.Add(new SkippedArchiveRow(i, StreamDataSkipReason.LowerPrecedence));
                continue;
            }

            var match = candidates.FindIndex(r => Holds(r, s, keyColumns, includeOrigin));
            if (match >= 0)
            {
                candidates.RemoveAt(match);
                continue;
            }

            // The stored row stays. When the guard ranks by origin, the stored row is manual and the write was
            // automated, that is the permanence of a manual value; otherwise the ranking columns decided.
            var storedOrigin = includeOrigin && ranksByOrigin
                ? ArchiveRowOriginColumns.FromStoredValue(candidates[0].TryGetValue(Constants.RowOrigin, out var o) ? o : null)
                : ArchiveRowOrigin.Automated;
            skipped.Add(new SkippedArchiveRow(i,
                storedOrigin == ArchiveRowOrigin.Manual && s.Origin != ArchiveRowOrigin.Manual
                    ? StreamDataSkipReason.ManualValueProtected
                    : StreamDataSkipReason.LowerPrecedence));
        }

        return skipped;
    }

    private static bool Holds(IReadOnlyDictionary<string, object?> stored, SentRow sent, IReadOnlyList<string> keyColumns,
        bool includeOrigin)
    {
        foreach (var column in keyColumns)
        {
            var storedValue = stored.TryGetValue(column, out var v) ? v : null;
            var sentValue = sent.Values.TryGetValue(column, out var w) ? w : null;
            if (!ValuesEqual(storedValue, sentValue))
            {
                return false;
            }
        }

        return !includeOrigin
               || ArchiveRowOriginColumns.FromStoredValue(stored.TryGetValue(Constants.RowOrigin, out var origin) ? origin : null)
               == sent.Origin;
    }

    private static string Key(string? rtId, string? ckTypeId, long? start, long? end)
        => string.Join("\u001f", rtId ?? "", ckTypeId ?? "", start?.ToString(CultureInfo.InvariantCulture) ?? "",
            end?.ToString(CultureInfo.InvariantCulture) ?? "");

    /// <summary>Equality as CrateDB stores the value; <c>null</c> equals only <c>null</c>.</summary>
    internal static bool ValuesEqual(object? stored, object? sent)
    {
        if (stored is null || stored is DBNull)
        {
            return sent is null or DBNull;
        }

        if (sent is null or DBNull)
        {
            return false;
        }

        // The raw insert path passes values on as the writer sent them: a time or a number can arrive as a string.
        if (sent is string text)
        {
            if (IsNumber(stored) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return ValuesEqual(stored, number);
            }

            if ((IsTime(stored) || stored is long)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                return AsUtcMilliseconds(stored) == time.ToUnixTimeMilliseconds();
            }
        }

        if (AsUtcMilliseconds(stored) is { } storedMs && AsUtcMilliseconds(sent) is { } sentMs && IsTime(stored) && IsTime(sent))
        {
            return storedMs == sentMs;
        }

        if (IsNumber(stored) && IsNumber(sent))
        {
            try
            {
                return Convert.ToDecimal(stored, CultureInfo.InvariantCulture) == Convert.ToDecimal(sent, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return Convert.ToDouble(stored, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(sent, CultureInfo.InvariantCulture));
            }
        }

        if (stored is string s1 && sent is string s2)
        {
            return string.Equals(s1, s2, StringComparison.Ordinal);
        }

        // An enum written as its name and stored as its key, or the reverse, compares by the key.
        if (sent is Enum e && IsNumber(stored))
        {
            return Convert.ToInt64(e, CultureInfo.InvariantCulture) == Convert.ToInt64(stored, CultureInfo.InvariantCulture);
        }

        return stored.Equals(sent);
    }

    private static bool IsTime(object value) => value is DateTime or DateTimeOffset;

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal;

    private static long? AsUtcMilliseconds(object? value) => value switch
    {
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime())
            .ToUnixTimeMilliseconds(),
        DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
        long ms => ms,
        _ => null
    };
}
