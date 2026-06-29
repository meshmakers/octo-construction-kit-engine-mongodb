using System.Collections.Generic;
using System.Text;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pure-function SQL for the rollup-internal computed-column evaluation pass (concept §11, approach
/// a). After the orchestrator writes a bucket's aggregate columns, the repository reads those rows
/// back (<see cref="BuildSelect"/>), evaluates the computed columns in .NET, and writes the result
/// back per row (<see cref="BuildUpdate"/>). No I/O — SQL strings in, SQL strings out.
/// </summary>
internal static class RollupComputedColumnSqlBuilder
{
    /// <summary>
    /// <c>SELECT "rtid", "cktypeid", &lt;aggregate columns&gt; FROM {table}
    /// WHERE "window_start" = … AND "window_end" = …;</c> — the rows just written for the bucket,
    /// carrying the aggregate values the computed formulas reference.
    /// </summary>
    public static string BuildSelect(
        string qualifiedTable,
        IReadOnlyList<string> aggregateColumns,
        System.DateTime bucketStart,
        System.DateTime bucketEnd)
    {
        var sb = new StringBuilder("SELECT \"")
            .Append(Constants.RtId).Append("\", \"").Append(Constants.CkTypeId).Append('"');

        foreach (var col in aggregateColumns)
        {
            sb.Append(", \"").Append(col).Append('"');
        }

        sb.Append(" FROM ").Append(qualifiedTable)
          .Append(" WHERE \"").Append(Constants.WindowStart).Append("\" = ").Append(CrateSqlLiteral.Timestamp(bucketStart))
          .Append(" AND \"").Append(Constants.WindowEnd).Append("\" = ").Append(CrateSqlLiteral.Timestamp(bucketEnd))
          .Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// <c>UPDATE {table} SET "c1" = …, "c2" = … WHERE "window_start" = … AND "window_end" = …
    /// AND "rtid" = … AND "cktypeid" = …;</c> — writes one bucket row's computed cells. Computed
    /// values are finite numbers / booleans / timestamps / NULL (the formula engine already mapped
    /// NaN and the null sentinel to NULL), so inline literals are safe.
    /// </summary>
    public static string BuildUpdate(
        string qualifiedTable,
        IReadOnlyList<(string Column, object? Value)> assignments,
        string rtId,
        string ckTypeId,
        System.DateTime bucketStart,
        System.DateTime bucketEnd)
    {
        var sb = new StringBuilder("UPDATE ").Append(qualifiedTable).Append(" SET ");
        for (var i = 0; i < assignments.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"').Append(assignments[i].Column).Append("\" = ").Append(CrateSqlLiteral.Value(assignments[i].Value));
        }

        sb.Append(" WHERE \"").Append(Constants.WindowStart).Append("\" = ").Append(CrateSqlLiteral.Timestamp(bucketStart))
          .Append(" AND \"").Append(Constants.WindowEnd).Append("\" = ").Append(CrateSqlLiteral.Timestamp(bucketEnd))
          .Append(" AND \"").Append(Constants.RtId).Append("\" = ").Append(CrateSqlLiteral.String(rtId))
          .Append(" AND \"").Append(Constants.CkTypeId).Append("\" = ").Append(CrateSqlLiteral.String(ckTypeId))
          .Append(';');
        return sb.ToString();
    }
}
