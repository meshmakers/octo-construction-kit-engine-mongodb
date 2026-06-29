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
    /// <param name="sourceUsesWindowedStorage">
    /// True when the source archive uses the windowed <c>(window_start, window_end)</c> storage
    /// layout (either a <c>TimeRangeArchive</c> or a <c>RollupArchive</c>). In that case the time
    /// predicate is the fully-contained rule <c>window_start &gt;= B_start AND window_end &lt;= B_end</c>
    /// and <c>was_updated</c> from the source is propagated via <c>MAX(was_updated)</c> so retro-
    /// corrections cascade. False ⇒ raw archive with the single <c>timestamp</c> column and the
    /// half-open <c>timestamp &gt;= B_start AND timestamp &lt; B_end</c> predicate. Phase 8 / concept-time-
    /// range §7.
    /// </param>
    public static string Build(
        string sourceTable,
        string targetTable,
        string rollupCkTypeId,
        IReadOnlyList<CkRollupAggregationSpec> aggregations,
        DateTime bucketStart,
        DateTime bucketEnd,
        bool sourceUsesWindowedStorage)
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
        // Phase-7 unification: rollup tables use the windowed (window_start, window_end) shape
        // shared with TimeRangeArchive. The source side now also branches (Phase 8 / concept §7):
        // raw archive ⇒ single `timestamp` column, windowed source ⇒ (window_start, window_end)
        // with `was_updated` propagation.
        sb.Append("INSERT INTO ").Append(targetTable).Append(" (")
          .Append('"').Append(Constants.WindowStart).Append("\", ")
          .Append('"').Append(Constants.WindowEnd).Append("\", ")
          .Append('"').Append(Constants.RtId).Append("\", ")
          .Append('"').Append(Constants.CkTypeId).Append("\", ")
          .Append('"').Append(Constants.RtWellKnownName).Append('"');
        if (sourceUsesWindowedStorage)
        {
            sb.Append(", \"").Append(Constants.WasUpdated).Append('"');
        }
        foreach (var (_, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(", \"").Append(t.ColumnName).Append('"');
            }
        }
        // Phase 6: forward aggregation always writes generation 0 (the steady-state generation);
        // only the recompute executor stamps higher generations when copying staging into live.
        sb.Append(", \"").Append(Constants.Generation).Append('"');
        sb.AppendLine(")");

        // ---- SELECT ... FROM source WHERE <time-predicate> GROUP BY rtId ----
        sb.Append("SELECT '").Append(bucketStartLiteral).Append("'::timestamp AS \"").Append(Constants.WindowStart).Append("\", ")
          .Append('\'').Append(bucketEndLiteral).Append("'::timestamp AS \"").Append(Constants.WindowEnd).Append("\", ")
          .Append('"').Append(Constants.RtId).Append("\", ")
          .Append('\'').Append(EscapeLiteral(rollupCkTypeId)).Append("' AS \"").Append(Constants.CkTypeId).Append("\", ")
          .Append("MAX(\"").Append(Constants.RtWellKnownName).Append("\") AS \"").Append(Constants.RtWellKnownName).Append('"');
        if (sourceUsesWindowedStorage)
        {
            // Inherit retro-correction flag from source — any flagged source row taints the rollup
            // row. Concept §6 + §7. The conflict branch below additionally flips to TRUE on every
            // re-aggregation regardless of source state.
            sb.Append(", MAX(\"").Append(Constants.WasUpdated).Append("\") AS \"").Append(Constants.WasUpdated).Append('"');
        }
        foreach (var (sourceColumn, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(", ").Append(t.Function).Append("(\"").Append(sourceColumn).Append("\") AS \"").Append(t.ColumnName).Append('"');
            }
        }
        sb.Append(", 0 AS \"").Append(Constants.Generation).Append('"');
        sb.AppendLine().Append("FROM ").AppendLine(sourceTable);
        if (sourceUsesWindowedStorage)
        {
            // Fully-contained window predicate (concept §7): source windows must fit entirely
            // inside the target bucket. Straddling source rows drop out — operators must size
            // target buckets as multiples of the source window length.
            sb.Append("WHERE \"").Append(Constants.WindowStart).Append("\" >= '").Append(bucketStartLiteral).Append("'::timestamp ")
              .Append("AND \"").Append(Constants.WindowEnd).Append("\" <= '").Append(bucketEndLiteral).AppendLine("'::timestamp");
        }
        else
        {
            sb.Append("WHERE \"").Append(Constants.Timestamp).Append("\" >= '").Append(bucketStartLiteral).Append("'::timestamp ")
              .Append("AND \"").Append(Constants.Timestamp).Append("\" < '").Append(bucketEndLiteral).AppendLine("'::timestamp");
        }
        sb.Append("GROUP BY \"").Append(Constants.RtId).AppendLine("\"");

        // ---- ON CONFLICT (window_start, window_end, rtId, ckTypeId) DO UPDATE SET ... ----
        // Same conflict key as TimeRangeArchive — same was_updated semantics: the orchestrator
        // re-running a bucket (after rewind, or a crash that didn't commit the watermark) is a
        // correction; flip the flag to signal "this row was rewritten at some point".
        // Conflict key includes generation: rollup tables key on (window_start, window_end, rtid,
        // ckTypeId, generation) (Phase 6) so a forward re-aggregation collapses onto the generation-0
        // row, while recomputed higher-generation rows for the same window are left untouched.
        sb.Append("ON CONFLICT (\"").Append(Constants.WindowStart).Append("\", \"").Append(Constants.WindowEnd).Append("\", \"").Append(Constants.RtId).Append("\", \"").Append(Constants.CkTypeId).Append("\", \"").Append(Constants.Generation).AppendLine("\") DO UPDATE SET");
        sb.Append("    \"").Append(Constants.RtWellKnownName).Append("\" = EXCLUDED.\"").Append(Constants.RtWellKnownName).Append('"');
        foreach (var (_, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(",\n    \"").Append(t.ColumnName).Append("\" = EXCLUDED.\"").Append(t.ColumnName).Append('"');
            }
        }
        sb.Append(",\n    \"").Append(Constants.WasUpdated).Append("\" = TRUE");
        sb.Append(",\n    \"").Append(Constants.RtChangedDateTime).Append("\" = CURRENT_TIMESTAMP;");

        return sb.ToString();
    }

    /// <summary>Escapes a single-quoted SQL string literal by doubling embedded single-quotes.</summary>
    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
