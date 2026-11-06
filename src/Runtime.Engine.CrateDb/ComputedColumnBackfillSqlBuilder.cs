using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <c>SELECT "&lt;key...&gt;", "&lt;value...&gt;" FROM {table} [WHERE &lt;keyset cursor&gt;]
    /// ORDER BY "&lt;key...&gt;" [LIMIT n];</c> — the key columns (needed to address each row on the
    /// write back) plus the value columns the formulas reference, ordered by the natural key.
    /// <para>
    /// Paging is <b>keyset</b> (cursor), not OFFSET: pass the previous page's last-row key values as
    /// <paramref name="cursor"/> and the query seeks straight past them via the key index, so each
    /// page costs O(<paramref name="limit"/>) regardless of table size. The former <c>OFFSET</c> form
    /// made CrateDB collect + sort the whole table up to the offset on every page, so a large archive
    /// tripped the query circuit breaker on its final pages (AB#4189).
    /// </para>
    /// </summary>
    public static string BuildSelect(
        string qualifiedTable,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> valueColumns,
        int? limit = null,
        IReadOnlyList<(string Column, object? Value)>? cursor = null,
        TimeSlice? slice = null,
        string? rowFilter = null)
    {
        var sb = new StringBuilder("SELECT ");
        AppendColumnList(sb, keyColumns, valueColumns);
        sb.Append(" FROM ").Append(qualifiedTable);

        var predicates = new List<string>(2);
        if (slice is { } s)
        {
            // AB#8002: bound the page to one time slice, so CrateDB filters and sorts only the rows
            // of that slice — not every row after the cursor in the whole table.
            predicates.Add(
                $"\"{s.Column}\" >= {CrateSqlLiteral.Value(s.From)} AND \"{s.Column}\" < {CrateSqlLiteral.Value(s.To)}");
        }

        if (cursor is { Count: > 0 })
        {
            predicates.Add(BuildKeysetPredicate(cursor));
        }

        if (!string.IsNullOrEmpty(rowFilter))
        {
            // AB#8331: the repair walk only reads the rows it has to re-evaluate.
            predicates.Add(rowFilter);
        }

        if (predicates.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }

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
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// <c>("{target}" IS NULL AND ("{op1}" IS NOT NULL OR "{op2}" IS NOT NULL …))</c> — the rows a computed
    /// column repair re-evaluates (AB#8331): the cell is NULL although at least one operand the formula
    /// references has a value. A formula without operands has nothing to repair (<c>FALSE</c>).
    /// </summary>
    public static string BuildRepairFilter(string targetColumn, IReadOnlyList<string> operandColumns)
    {
        if (operandColumns.Count == 0)
        {
            return "FALSE";
        }

        var operands = string.Join(" OR ", operandColumns.Select(c => $"\"{c}\" IS NOT NULL"));
        return $"(\"{targetColumn}\" IS NULL AND ({operands}))";
    }

    /// <summary>
    /// <c>SELECT min("{time}") AS "lo", max("{time}") AS "hi" FROM {table};</c> — the time span a
    /// sliced backfill walks (AB#8002). Both are NULL for an empty table.
    /// </summary>
    public static string BuildTimeBounds(string qualifiedTable, string timeColumn) =>
        $"SELECT min(\"{timeColumn}\") AS \"lo\", max(\"{timeColumn}\") AS \"hi\" FROM {qualifiedTable};";

    /// <summary>
    /// <c>SELECT min("{time}") AS "lo" FROM {table} WHERE "{time}" &gt;= {from};</c> — the first time
    /// key at or after <paramref name="from"/>, so a sliced backfill jumps over a gap in the data with
    /// one lookup instead of one query per empty slice (AB#8002). NULL when nothing follows.
    /// </summary>
    public static string BuildNextTimeAtOrAfter(string qualifiedTable, string timeColumn, DateTime from) =>
        $"SELECT min(\"{timeColumn}\") AS \"lo\" FROM {qualifiedTable} WHERE \"{timeColumn}\" >= {CrateSqlLiteral.Value(from)};";

    /// <summary>
    /// A row-value greater-than over the ordered key columns, expanded into the portable OR-of-AND
    /// form <c>(k1 &gt; v1) OR (k1 = v1 AND k2 &gt; v2) OR …</c>. This is the keyset cursor that
    /// replaces OFFSET paging — with the key index CrateDB seeks straight to the row after the cursor
    /// instead of collecting every preceding row. Values are formatted via
    /// <see cref="CrateSqlLiteral.Value"/> exactly as <see cref="BuildUpdate"/> formats the same
    /// key-column values, so the cursor round-trips read-back keys identically to the write path. The
    /// key is the archive's primary key (raw <c>timestamp, rtid, cktypeid</c>; windowed
    /// <c>window_start, window_end, rtid, cktypeid</c>) so a strict <c>&gt;</c> never skips a row.
    /// </summary>
    private static string BuildKeysetPredicate(IReadOnlyList<(string Column, object? Value)> cursor)
    {
        var ors = new List<string>(cursor.Count);
        for (var i = 0; i < cursor.Count; i++)
        {
            var terms = new List<string>(i + 1);
            for (var j = 0; j < i; j++)
            {
                terms.Add($"\"{cursor[j].Column}\" = {CrateSqlLiteral.Value(cursor[j].Value)}");
            }

            terms.Add($"\"{cursor[i].Column}\" > {CrateSqlLiteral.Value(cursor[i].Value)}");
            ors.Add("(" + string.Join(" AND ", terms) + ")");
        }

        return "(" + string.Join(" OR ", ors) + ")";
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

    /// <summary>
    /// <c>UPDATE {table} SET "{target}" = $1 WHERE "{k1}" = $2 AND "{k2}" = $3 …</c> — the
    /// <b>parameterised</b> single-target update run once per row via CrateDB's bulk path
    /// (one prepared statement, one round-trip for the whole page's rows). Positional placeholders
    /// bind <c>[computedValue, key0, key1, …]</c>. Replaces the per-row literal <see cref="BuildUpdate"/>
    /// in the backfill: CrateDB executes the bulk far cheaper than N individual statements
    /// (~500× on 4k rows measured), so a large-archive backfill drops from tens of minutes to seconds.
    /// </summary>
    public static string BuildBulkUpdate(
        string qualifiedTable, string targetColumn, IReadOnlyList<string> keyColumns)
    {
        var sb = new StringBuilder("UPDATE ").Append(qualifiedTable)
            .Append(" SET \"").Append(targetColumn).Append("\" = $1 WHERE ");
        for (var i = 0; i < keyColumns.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append('"').Append(keyColumns[i]).Append("\" = $").Append(i + 2);
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

    /// <summary>A half-open slice <c>[From, To)</c> of an archive's time key (AB#8002).</summary>
    public readonly record struct TimeSlice(string Column, DateTime From, DateTime To);
}
