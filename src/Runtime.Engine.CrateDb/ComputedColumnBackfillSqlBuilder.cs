using System.Collections.Generic;
using System.Text;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pure-function SQL for the active-archive computed-column backfill (AB#4189 Phase 7, §8). After a
/// computed column is added to (or its formula changed on) a live raw / time-range archive, the
/// backfill pages through the existing rows (<see cref="BuildSelect"/>), evaluates the computed
/// column(s) in .NET via the shared formula engine, and writes the result back per row
/// (<see cref="BuildUpdate"/>). No I/O — SQL strings in, SQL strings out.
/// <para>
/// Row-keyed rather than window-keyed (cf. <see cref="RollupComputedColumnSqlBuilder"/>): the key
/// columns are supplied by the caller so the same builder serves a raw archive
/// (<c>timestamp, rtid, cktypeid</c>) and a time-range archive
/// (<c>window_start, window_end, rtid, cktypeid</c>) without the builder needing to know the shape.
/// </para>
/// </summary>
internal static class ComputedColumnBackfillSqlBuilder
{
    /// <summary>
    /// <c>SELECT "&lt;key...&gt;", "&lt;value...&gt;" FROM {table} ORDER BY "&lt;key...&gt;"
    /// [LIMIT n OFFSET o];</c> — the key columns (needed to address each row on the write back) plus
    /// the value columns the formulas reference. A deterministic <c>ORDER BY</c> over the key columns
    /// makes the optional <paramref name="limit"/> / <paramref name="offset"/> paging stable.
    /// </summary>
    public static string BuildSelect(
        string qualifiedTable,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns,
        int? limit = null,
        int? offset = null)
    {
        var sb = new StringBuilder("SELECT ");
        AppendColumnList(sb, keyColumns, valueColumns);
        sb.Append(" FROM ").Append(qualifiedTable);

        if (keyColumns.Count > 0)
        {
            sb.Append(" ORDER BY ");
            for (var i = 0; i < keyColumns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(keyColumns[i]).Append('"');
            }
        }

        if (limit is { } l) sb.Append(" LIMIT ").Append(l);
        if (offset is { } o) sb.Append(" OFFSET ").Append(o);
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// <c>UPDATE {table} SET "c1" = …, "c2" = … WHERE "k1" = … AND "k2" = … …;</c> — writes one
    /// row's computed cells, addressed by the row's key-column values read back by
    /// <see cref="BuildSelect"/>.
    /// </summary>
    public static string BuildUpdate(
        string qualifiedTable,
        IReadOnlyList<(string Column, object? Value)> assignments,
        IReadOnlyList<(string Column, object? Value)> keyPredicates)
    {
        var sb = new StringBuilder("UPDATE ").Append(qualifiedTable).Append(" SET ");
        for (var i = 0; i < assignments.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"').Append(assignments[i].Column).Append("\" = ").Append(CrateSqlLiteral.Value(assignments[i].Value));
        }

        sb.Append(" WHERE ");
        for (var i = 0; i < keyPredicates.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append('"').Append(keyPredicates[i].Column).Append("\" = ").Append(CrateSqlLiteral.Value(keyPredicates[i].Value));
        }

        sb.Append(';');
        return sb.ToString();
    }

    private static void AppendColumnList(
        StringBuilder sb, IReadOnlyList<string> keyColumns, IReadOnlyList<string> valueColumns)
    {
        var first = true;
        foreach (var col in keyColumns)
        {
            if (!first) sb.Append(", ");
            sb.Append('"').Append(col).Append('"');
            first = false;
        }

        foreach (var col in valueColumns)
        {
            if (!first) sb.Append(", ");
            sb.Append('"').Append(col).Append('"');
            first = false;
        }
    }
}
