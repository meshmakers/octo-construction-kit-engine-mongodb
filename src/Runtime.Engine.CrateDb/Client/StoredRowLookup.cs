using System.Text;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// The primary key of one archive row (AB#8622): entity, CK type and timestamp (raw archive) or window
/// (time-range archive, <see cref="End" /> set).
/// </summary>
public readonly record struct StoredRowKey(string RtId, string CkTypeId, DateTime Start, DateTime? End)
{
    /// <summary>The key in the form <see cref="ConflictOutcome" /> matches returned rows with.</summary>
    public string Value => ConflictOutcome.Key(RtId, CkTypeId, ConflictOutcome.AsUtcMilliseconds(Start),
        End is { } end ? ConflictOutcome.AsUtcMilliseconds(end) : null);

    /// <summary>The key of a row CrateDB returned, or <c>null</c> when a key column is missing.</summary>
    internal static string? Of(IReadOnlyDictionary<string, object?> row, bool windowed)
    {
        if (!row.TryGetValue(Constants.RtId, out var rtId) || rtId is not string rt ||
            !row.TryGetValue(Constants.CkTypeId, out var ckTypeId) || ckTypeId is not string ck)
        {
            return null;
        }

        var start = ConflictOutcome.AsUtcMilliseconds(
            row.TryGetValue(windowed ? Constants.WindowStart : Constants.Timestamp, out var s) ? s : null);
        var end = windowed
            ? ConflictOutcome.AsUtcMilliseconds(row.TryGetValue(Constants.WindowEnd, out var e) ? e : null)
            : null;
        return start is null || (windowed && end is null) ? null : ConflictOutcome.Key(rt, ck, start, end);
    }
}

/// <summary>
/// Reads the stored values of named columns for a set of archive rows by primary key (AB#8622: the inputs of a
/// computed column that a partial delivery does not send). Every key is an equality on all primary key columns,
/// so CrateDB answers with a real-time primary-key lookup: a row written a moment before is seen without a
/// table refresh.
/// </summary>
internal static class StoredRowLookup
{
    /// <summary>Keys per statement: four bind parameters each, far below the statement limit.</summary>
    internal const int MaxKeysPerStatement = 250;

    /// <summary>UTC as the archive stores it; an unspecified kind is read as UTC (as <see cref="ConflictOutcome" /> does).</summary>
    internal static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
        : value.ToUniversalTime();

    /// <summary>
    /// <c>SELECT</c> of the key columns and <paramref name="columns" /> for <paramref name="keyCount" /> keys, one
    /// <c>(key = $n AND …)</c> term per key joined by <c>OR</c>. Parameters per key: start, [end,] rtid, cktypeid.
    /// </summary>
    internal static string BuildSelectSql(string qualifiedTable, bool windowed, IReadOnlyList<string> columns,
        int keyCount)
    {
        var keyColumns = windowed
            ? new[] { Constants.WindowStart, Constants.WindowEnd, Constants.RtId, Constants.CkTypeId }
            : new[] { Constants.Timestamp, Constants.RtId, Constants.CkTypeId };

        var sb = new StringBuilder(64 + (keyColumns.Length + columns.Count) * 16 + keyCount * keyColumns.Length * 24);
        sb.Append("SELECT ");
        sb.Append(string.Join(", ", keyColumns.Concat(columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal)))
            .Select(c => $"\"{c}\"")));
        sb.Append(" FROM ").Append(qualifiedTable).Append(" WHERE ");

        var parameter = 1;
        for (var k = 0; k < keyCount; k++)
        {
            sb.Append(k == 0 ? "(" : " OR (");
            for (var c = 0; c < keyColumns.Length; c++)
            {
                if (c > 0)
                {
                    sb.Append(" AND ");
                }

                sb.Append('"').Append(keyColumns[c]).Append("\" = $").Append(parameter++);
            }

            sb.Append(')');
        }

        return sb.ToString();
    }
}
