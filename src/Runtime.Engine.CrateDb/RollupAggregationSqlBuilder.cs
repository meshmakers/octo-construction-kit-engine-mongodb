using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pure-function generator for rollup-aggregation SQL: a single
/// <c>INSERT INTO target (...) SELECT ... FROM source ... GROUP BY rtId ON CONFLICT DO UPDATE</c>
/// statement that turns one source-archive bucket into one rollup row per entity. Rollup-archives
/// concept §5.
/// </summary>
/// <remarks>
/// The natural key <c>(timestamp, rtId, ckTypeId)</c> matches the archive-table primary key
/// emitted by <see cref="ArchiveDdlGenerator"/>, so the conflict clause collapses re-aggregations
/// of the same bucket via upsert. Inputs are validated; output is one self-terminated SQL
/// statement.
/// </remarks>
internal static class RollupAggregationSqlBuilder
{
    /// <summary>
    /// Builds the upsert statement for one aggregation bucket. The caller supplies already-quoted,
    /// schema-qualified table identifiers (use <see cref="TenantSchema.QualifiedArchiveTable"/>).
    /// </summary>
    /// <param name="sourceTable">Schema-qualified, double-quoted source archive table.</param>
    /// <param name="targetTable">Schema-qualified, double-quoted rollup archive table.</param>
    /// <param name="rollupCkTypeId">
    /// Constant value written into the rollup's <c>ckTypeId</c> column. Stored so consumers can tell
    /// rollup rows apart from raw rows when both share a CrateDB cluster.
    /// </param>
    /// <param name="aggregations">User-defined aggregation specs. Must contain at least one entry.</param>
    /// <param name="bucketStart">Inclusive start of the source row range.</param>
    /// <param name="bucketEnd">Exclusive end of the source row range; also written as the target row's timestamp.</param>
    public static string Build(
        string sourceTable,
        string targetTable,
        string rollupCkTypeId,
        IReadOnlyList<CkRollupAggregationSpec> aggregations,
        DateTime bucketStart,
        DateTime bucketEnd)
    {
        if (string.IsNullOrWhiteSpace(sourceTable)) throw new ArgumentException("sourceTable must not be empty.", nameof(sourceTable));
        if (string.IsNullOrWhiteSpace(targetTable)) throw new ArgumentException("targetTable must not be empty.", nameof(targetTable));
        if (string.IsNullOrWhiteSpace(rollupCkTypeId)) throw new ArgumentException("rollupCkTypeId must not be empty.", nameof(rollupCkTypeId));
        if (aggregations is null || aggregations.Count == 0) throw new ArgumentException("At least one aggregation is required.", nameof(aggregations));
        if (bucketEnd <= bucketStart) throw new ArgumentException("bucketEnd must be greater than bucketStart.", nameof(bucketEnd));

        // Resolve every spec to its (sourceColumn, target[]) pair once.
        var resolved = new List<(string SourceColumn, IReadOnlyList<RollupTargetColumn> Targets)>(aggregations.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in aggregations)
        {
            var (sourceColumn, targets) = RollupAggregationColumns.Resolve(spec);
            foreach (var t in targets)
            {
                if (!seen.Add(t.ColumnName))
                {
                    throw new ArgumentException(
                        $"Duplicate target column '{t.ColumnName}' produced by aggregations — pick distinct TargetColumnName values.",
                        nameof(aggregations));
                }
            }
            resolved.Add((sourceColumn, targets));
        }

        var sb = new StringBuilder();
        var bucketEndLiteral = bucketEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var bucketStartLiteral = bucketStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        // ---- INSERT INTO target (...columns...) ----
        sb.Append("INSERT INTO ").Append(targetTable).Append(" (")
          .Append('"').Append(Constants.Timestamp).Append("\", ")
          .Append('"').Append(Constants.RtId).Append("\", ")
          .Append('"').Append(Constants.CkTypeId).Append("\", ")
          .Append('"').Append(Constants.RtWellKnownName).Append('"');
        foreach (var (_, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(", \"").Append(t.ColumnName).Append('"');
            }
        }
        sb.AppendLine(")");

        // ---- SELECT ... FROM source WHERE timestamp ∈ bucket GROUP BY rtId ----
        sb.Append("SELECT '").Append(bucketEndLiteral).Append("'::timestamp AS \"").Append(Constants.Timestamp).Append("\", ")
          .Append('"').Append(Constants.RtId).Append("\", ")
          .Append('\'').Append(EscapeLiteral(rollupCkTypeId)).Append("' AS \"").Append(Constants.CkTypeId).Append("\", ")
          .Append("MAX(\"").Append(Constants.RtWellKnownName).Append("\") AS \"").Append(Constants.RtWellKnownName).Append('"');
        foreach (var (sourceColumn, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(", ").Append(t.Function).Append("(\"").Append(sourceColumn).Append("\") AS \"").Append(t.ColumnName).Append('"');
            }
        }
        sb.AppendLine().Append("FROM ").AppendLine(sourceTable);
        sb.Append("WHERE \"").Append(Constants.Timestamp).Append("\" >= '").Append(bucketStartLiteral).Append("'::timestamp ")
          .Append("AND \"").Append(Constants.Timestamp).Append("\" < '").Append(bucketEndLiteral).AppendLine("'::timestamp");
        sb.Append("GROUP BY \"").Append(Constants.RtId).AppendLine("\"");

        // ---- ON CONFLICT (timestamp, rtId, ckTypeId) DO UPDATE SET ... ----
        sb.Append("ON CONFLICT (\"").Append(Constants.Timestamp).Append("\", \"").Append(Constants.RtId).Append("\", \"").Append(Constants.CkTypeId).AppendLine("\") DO UPDATE SET");
        sb.Append("    \"").Append(Constants.RtWellKnownName).Append("\" = EXCLUDED.\"").Append(Constants.RtWellKnownName).Append('"');
        foreach (var (_, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(",\n    \"").Append(t.ColumnName).Append("\" = EXCLUDED.\"").Append(t.ColumnName).Append('"');
            }
        }
        sb.Append(",\n    \"").Append(Constants.RtChangedDateTime).Append("\" = CURRENT_TIMESTAMP;");

        return sb.ToString();
    }

    /// <summary>Escapes a single-quoted SQL string literal by doubling embedded single-quotes.</summary>
    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
