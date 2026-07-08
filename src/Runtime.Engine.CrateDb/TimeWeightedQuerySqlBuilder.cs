using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pure-function generator for ad-hoc TimeWeightedAvg queries over a <em>raw</em> (event-based)
/// archive — the query-time counterpart of <see cref="RollupAggregationSqlBuilder"/>'s LOCF
/// bucket statement, with the query window <c>[from, to)</c> playing the bucket role. Concept
/// <c>concept-time-weighted-aggregation.md</c> §6.2 (AB#4336).
/// </summary>
/// <remarks>
/// Statement shape: a carry-in row per <c>rtId</c> (latest observation before <c>from</c>,
/// bounded by the carry lookback) is unioned with the in-window events; each observation is
/// weighted by the interval to the next one via <c>LEAD</c> (capped at <c>to</c>); the outer
/// SELECT aggregates — grouped by the requested group columns, or into a single row when none
/// are given. Plain aggregations in the same query exclude the carry row via <c>is_carry</c>,
/// keeping their semantics identical to the standard single-scan statement. TWA values are
/// computed directly as <c>integral / NULLIF(covered, 0)</c> — nothing is materialised.
/// </remarks>
internal static class TimeWeightedQuerySqlBuilder
{
    /// <summary>One time-weighted output column: the physical source column and its SQL alias.</summary>
    public sealed record TwaColumn(string SourceColumn, string OutputAlias);

    /// <summary>
    /// One plain aggregation output column riding along in the same statement.
    /// <paramref name="SqlFunction"/> is the SQL aggregate keyword (<c>AVG</c>, <c>MIN</c>, …).
    /// </summary>
    public sealed record PlainColumn(string SqlFunction, string SourceColumn, string OutputAlias);

    /// <summary>
    /// Builds the SELECT statement. The caller supplies the already-quoted, schema-qualified
    /// source table (see <see cref="TenantSchema.QualifiedArchiveTable"/>) and pre-resolved
    /// physical column names; output aliases become the result-row keys.
    /// </summary>
    /// <param name="sourceTable">Schema-qualified, double-quoted raw archive table.</param>
    /// <param name="ckTypeId">
    /// Optional <c>ckTypeId</c> predicate value (SemanticVersionedFullName form); null/empty ⇒ no
    /// predicate.
    /// </param>
    /// <param name="from">Inclusive window start — also the carry-in virtual event timestamp.</param>
    /// <param name="to">Exclusive window end — caps the last observation's interval.</param>
    /// <param name="twaColumns">Time-weighted output columns. Must contain at least one entry.</param>
    /// <param name="plainColumns">Plain aggregations sharing the statement. May be empty.</param>
    /// <param name="groupByColumns">
    /// Physical group columns for the outer GROUP BY. Empty ⇒ one result row over all entities
    /// (LOCF weighting stays per <c>rtId</c> either way).
    /// </param>
    /// <param name="rtIds">Optional rtId scope applied to both the carry and the in-window scan.</param>
    /// <param name="carryLookback">Bound on the carry-in scan before <paramref name="from"/>.</param>
    /// <param name="fieldFilters">
    /// Optional pre-resolved field filters. They select the EVENT SET the LOCF weighting runs
    /// over — applied to both the carry and the in-window scan, so the opening state is the last
    /// observation that itself satisfies the filters.
    /// </param>
    public static string Build(
        string sourceTable,
        string? ckTypeId,
        DateTime from,
        DateTime to,
        IReadOnlyList<TwaColumn> twaColumns,
        IReadOnlyList<PlainColumn> plainColumns,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<string>? rtIds,
        TimeSpan carryLookback,
        IReadOnlyList<StreamDataFieldFilterDto>? fieldFilters = null)
    {
        if (string.IsNullOrWhiteSpace(sourceTable)) throw new ArgumentException("sourceTable must not be empty.", nameof(sourceTable));
        if (twaColumns is null || twaColumns.Count == 0) throw new ArgumentException("At least one time-weighted column is required.", nameof(twaColumns));
        if (to <= from) throw new ArgumentException("to must be greater than from.", nameof(to));
        if (carryLookback <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(carryLookback));

        var fromLiteral = from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var toLiteral = to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var carryFromLiteral = (from.ToUniversalTime() - carryLookback).ToString("O", CultureInfo.InvariantCulture);

        // Columns every event row must carry: TWA + plain source columns and the group columns
        // (rtid + rtwellknownname are always selected).
        var carried = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { Constants.RtId, Constants.RtWellKnownName };
        foreach (var c in twaColumns.Select(t => t.SourceColumn)
                     .Concat(plainColumns.Select(p => p.SourceColumn))
                     .Concat(groupByColumns))
        {
            if (seen.Add(c))
            {
                carried.Add(c);
            }
        }
        var carriedList = carried.Count > 0 ? ", " + string.Join(", ", carried.Select(c => $"\"{c}\"")) : string.Empty;

        var predicate = new StringBuilder();
        if (!string.IsNullOrEmpty(ckTypeId))
        {
            predicate.Append(" AND \"").Append(Constants.CkTypeId).Append("\" = '").Append(EscapeLiteral(ckTypeId!)).Append('\'');
        }
        if (rtIds is { Count: > 0 })
        {
            predicate.Append(" AND \"").Append(Constants.RtId).Append("\" IN (")
                .Append(string.Join(", ", rtIds.Select(id => $"'{EscapeLiteral(id)}'")))
                .Append(')');
        }
        if (fieldFilters is { Count: > 0 })
        {
            foreach (var filter in fieldFilters)
            {
                predicate.Append(" AND ").Append(CrateQueryCompiler.CompileFieldFilter(filter));
            }
        }

        var sb = new StringBuilder();

        // ---- outer SELECT: group columns + aggregates over the weighted event rows ----
        sb.Append("SELECT ");
        var first = true;
        foreach (var g in groupByColumns)
        {
            if (!first) sb.Append(", ");
            sb.Append('"').Append(g).Append('"');
            first = false;
        }
        foreach (var p in plainColumns)
        {
            if (!first) sb.Append(", ");
            // Plain aggregations must not see the carry-in virtual row — it lies outside the window.
            sb.Append(p.SqlFunction).Append("(CASE WHEN NOT \"is_carry\" THEN \"").Append(p.SourceColumn)
              .Append("\" END) AS \"").Append(p.OutputAlias).Append('"');
            first = false;
        }
        foreach (var t in twaColumns)
        {
            if (!first) sb.Append(", ");
            // integral / covered — NULL observations contribute to neither (concept §3).
            sb.Append("SUM(CASE WHEN \"").Append(t.SourceColumn).Append("\" IS NOT NULL THEN \"")
              .Append(t.SourceColumn).Append("\" * \"dt_ms\" END) / NULLIF(SUM(CASE WHEN \"")
              .Append(t.SourceColumn).Append("\" IS NOT NULL THEN \"dt_ms\" END), 0) AS \"")
              .Append(t.OutputAlias).Append('"');
            first = false;
        }

        // ---- weighted: interval to the next observation per rtId, capped at the window end ----
        // Ties on ts (a carry at `from` next to an in-window event at exactly `from`) order the
        // carry first (is_carry DESC) so it gets Δt = 0 instead of shadowing the real event.
        sb.AppendLine().AppendLine("FROM (")
          .Append("    SELECT \"").Append(Constants.RtId).Append("\", \"is_carry\"").Append(carriedList)
          .Append(",\n           COALESCE(LEAD(\"ts\") OVER (PARTITION BY \"").Append(Constants.RtId)
          .Append("\" ORDER BY \"ts\", \"is_carry\" DESC), '").Append(toLiteral)
          .AppendLine("'::timestamp)::bigint - \"ts\"::bigint AS \"dt_ms\"")
          .AppendLine("    FROM (")
          // ---- carry-in: latest observation before the window, per rtId, bounded lookback ----
          .Append("        SELECT \"").Append(Constants.RtId).Append("\", '").Append(fromLiteral)
          .Append("'::timestamp AS \"ts\", TRUE AS \"is_carry\"").Append(carriedList).AppendLine()
          .AppendLine("        FROM (")
          .Append("            SELECT \"").Append(Constants.RtId).Append("\", \"").Append(Constants.Timestamp).Append('"')
          .Append(carriedList)
          .Append(",\n                   ROW_NUMBER() OVER (PARTITION BY \"").Append(Constants.RtId)
          .Append("\" ORDER BY \"").Append(Constants.Timestamp).AppendLine("\" DESC) AS \"rn\"")
          .Append("            FROM ").AppendLine(sourceTable)
          .Append("            WHERE \"").Append(Constants.Timestamp).Append("\" < '").Append(fromLiteral)
          .Append("'::timestamp AND \"").Append(Constants.Timestamp).Append("\" >= '").Append(carryFromLiteral)
          .Append("'::timestamp").Append(predicate).AppendLine()
          .AppendLine("        ) \"carry\"")
          .AppendLine("        WHERE \"rn\" = 1")
          .AppendLine("        UNION ALL")
          // ---- in-window events ----
          .Append("        SELECT \"").Append(Constants.RtId).Append("\", \"").Append(Constants.Timestamp)
          .Append("\" AS \"ts\", FALSE AS \"is_carry\"").Append(carriedList).AppendLine()
          .Append("        FROM ").AppendLine(sourceTable)
          .Append("        WHERE \"").Append(Constants.Timestamp).Append("\" >= '").Append(fromLiteral)
          .Append("'::timestamp AND \"").Append(Constants.Timestamp).Append("\" < '").Append(toLiteral)
          .Append("'::timestamp").Append(predicate).AppendLine()
          .AppendLine("    ) \"events\"")
          .Append(") \"weighted\"");

        if (groupByColumns.Count > 0)
        {
            sb.AppendLine().Append("GROUP BY ")
              .Append(string.Join(", ", groupByColumns.Select(g => $"\"{g}\"")));
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>Escapes a single-quoted SQL string literal by doubling embedded single-quotes.</summary>
    private static string EscapeLiteral(object value) => value.ToString()!.Replace("'", "''");
}
