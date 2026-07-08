using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// <para>
/// The natural key <c>(timestamp, rtId, ckTypeId)</c> matches the archive-table primary key
/// emitted by <see cref="ArchiveDdlGenerator"/>, so the conflict clause collapses re-aggregations
/// of the same bucket via upsert. Inputs are validated; output is one self-terminated SQL
/// statement.
/// </para>
/// <para>
/// When the aggregations include <see cref="CkRollupFunction.TimeWeightedAvg"/> over a raw
/// (event-based) source, the statement is restructured around a nested LOCF sub-select: a
/// carry-in row per <c>rtId</c> (latest observation before the bucket, bounded by
/// <paramref name="carryLookback"/>) is unioned with the in-bucket events, each observation is
/// weighted by the interval to the next observation via <c>LEAD</c>, and the bucket materialises
/// an integral / covered-duration column pair. Plain aggregations in the same statement exclude
/// the carry row. See <c>concept-time-weighted-aggregation.md</c> (AB#4336) §5.
/// </para>
/// </remarks>
internal static class RollupAggregationSqlBuilder
{
    /// <summary>
    /// Default bound on how far before the bucket start the TimeWeightedAvg carry-in scan looks
    /// for the opening observation when the rollup declares no <c>CarryLookbackMs</c>.
    /// AB#4336 decision D1.
    /// </summary>
    public static readonly TimeSpan DefaultCarryLookback = TimeSpan.FromDays(35);

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
    /// <param name="rtIdScope">
    /// Optional single-entity scope (AB#4184): restricts the aggregation to one source entity.
    /// </param>
    /// <param name="carryLookback">
    /// Bound on the TimeWeightedAvg carry-in scan (LOCF opening state), from the rollup's
    /// <c>CarryLookbackMs</c>. Null ⇒ <see cref="DefaultCarryLookback"/>. Only consulted when the
    /// aggregations include <see cref="CkRollupFunction.TimeWeightedAvg"/> over a raw source.
    /// </param>
    public static string Build(
        string sourceTable,
        string targetTable,
        string rollupCkTypeId,
        IReadOnlyList<CkRollupAggregationSpec> aggregations,
        DateTime bucketStart,
        DateTime bucketEnd,
        bool sourceUsesWindowedStorage,
        string? rtIdScope = null,
        TimeSpan? carryLookback = null)
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

        var hasTimeWeighted = aggregations.Any(a => a.Function == CkRollupFunction.TimeWeightedAvg);
        if (hasTimeWeighted && !sourceUsesWindowedStorage)
        {
            return BuildWithLocfCarry(
                sourceTable, targetTable, rollupCkTypeId, resolved, bucketStart, bucketEnd,
                rtIdScope, carryLookback ?? DefaultCarryLookback);
        }

        return BuildStandard(
            sourceTable, targetTable, rollupCkTypeId, resolved, bucketStart, bucketEnd,
            sourceUsesWindowedStorage, rtIdScope);
    }

    /// <summary>
    /// The pre-AB#4336 single-scan statement: plain SQL aggregates over the bucket's source rows.
    /// Also used when TimeWeightedAvg aggregates a windowed source (each row's weight is its own
    /// window length — no LOCF needed, the windows are the coverage).
    /// </summary>
    private static string BuildStandard(
        string sourceTable,
        string targetTable,
        string rollupCkTypeId,
        IReadOnlyList<(string SourceColumn, IReadOnlyList<RollupTargetColumn> Targets)> resolved,
        DateTime bucketStart,
        DateTime bucketEnd,
        bool sourceUsesWindowedStorage,
        string? rtIdScope)
    {
        var sb = new StringBuilder();
        var bucketEndLiteral = bucketEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var bucketStartLiteral = bucketStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        // ---- INSERT INTO target (...columns...) ----
        // Phase-7 unification: rollup tables use the windowed (window_start, window_end) shape
        // shared with TimeRangeArchive. The source side now also branches (Phase 8 / concept §7):
        // raw archive ⇒ single `timestamp` column, windowed source ⇒ (window_start, window_end)
        // with `was_updated` propagation.
        AppendInsertColumnList(sb, targetTable, resolved, includeWasUpdated: sourceUsesWindowedStorage);

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
        // Window length of a windowed source row in epoch milliseconds — the TWA weight when the
        // source is a TimeRangeArchive (concept-time-weighted §3: the windows ARE the coverage).
        var windowLengthMs =
            $"(\"{Constants.WindowEnd}\"::bigint - \"{Constants.WindowStart}\"::bigint)";
        foreach (var (sourceColumn, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(", ");
                switch (t.Function)
                {
                    case RollupAggregationColumns.TimeWeightedIntegral:
                        sb.Append("SUM(CASE WHEN \"").Append(sourceColumn).Append("\" IS NOT NULL THEN \"")
                          .Append(sourceColumn).Append("\" * ").Append(windowLengthMs).Append(" END)");
                        break;
                    case RollupAggregationColumns.TimeWeightedDuration:
                        sb.Append("SUM(CASE WHEN \"").Append(sourceColumn).Append("\" IS NOT NULL THEN ")
                          .Append(windowLengthMs).Append(" END)");
                        break;
                    default:
                        sb.Append(t.Function).Append("(\"").Append(sourceColumn).Append("\")");
                        break;
                }
                sb.Append(" AS \"").Append(t.ColumnName).Append('"');
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
        // Per-rtId scoped recompute (AB#4184): restrict the aggregation to a single source entity so
        // only that entity's rollup rows are recomputed (and later swept) for the range.
        if (!string.IsNullOrEmpty(rtIdScope))
        {
            sb.Append("AND \"").Append(Constants.RtId).Append("\" = '").Append(EscapeLiteral(rtIdScope)).AppendLine("'");
        }
        sb.Append("GROUP BY \"").Append(Constants.RtId).AppendLine("\"");

        AppendConflictClause(sb, resolved);
        return sb.ToString();
    }

    /// <summary>
    /// The AB#4336 LOCF statement for TimeWeightedAvg over a raw (event-based) source. The inner
    /// sub-select unions a carry-in row per <c>rtId</c> (the latest observation before the bucket,
    /// bounded by <paramref name="carryLookback"/>, surfaced as a virtual event at the bucket
    /// start) with the in-bucket events; the middle sub-select weights each observation by the
    /// interval to the next one (<c>LEAD</c>, capped at the bucket end); the outer SELECT
    /// aggregates. Plain aggregations exclude the carry row via <c>is_carry</c> so their semantics
    /// are unchanged. A bucket with a carry but zero in-bucket events still produces a row — that
    /// is the "light stays on across a silent bucket" case the whole feature exists for.
    /// </summary>
    private static string BuildWithLocfCarry(
        string sourceTable,
        string targetTable,
        string rollupCkTypeId,
        IReadOnlyList<(string SourceColumn, IReadOnlyList<RollupTargetColumn> Targets)> resolved,
        DateTime bucketStart,
        DateTime bucketEnd,
        string? rtIdScope,
        TimeSpan carryLookback)
    {
        var sb = new StringBuilder();
        var bucketStartLiteral = bucketStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var bucketEndLiteral = bucketEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var carryFromLiteral = (bucketStart.ToUniversalTime() - carryLookback).ToString("O", CultureInfo.InvariantCulture);

        // Distinct source columns referenced by any spec — the event rows must carry all of them.
        var sourceColumns = new List<string>();
        var seenColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (sourceColumn, _) in resolved)
        {
            if (seenColumns.Add(sourceColumn))
            {
                sourceColumns.Add(sourceColumn);
            }
        }
        var sourceColumnList = string.Join(", ", sourceColumns.Select(c => $"\"{c}\""));

        var scopePredicate = string.IsNullOrEmpty(rtIdScope)
            ? string.Empty
            : $" AND \"{Constants.RtId}\" = '{EscapeLiteral(rtIdScope)}'";

        AppendInsertColumnList(sb, targetTable, resolved, includeWasUpdated: false);

        sb.Append("SELECT '").Append(bucketStartLiteral).Append("'::timestamp AS \"").Append(Constants.WindowStart).Append("\", ")
          .Append('\'').Append(bucketEndLiteral).Append("'::timestamp AS \"").Append(Constants.WindowEnd).Append("\", ")
          .Append('"').Append(Constants.RtId).Append("\", ")
          .Append('\'').Append(EscapeLiteral(rollupCkTypeId)).Append("' AS \"").Append(Constants.CkTypeId).Append("\", ")
          .Append("MAX(\"").Append(Constants.RtWellKnownName).Append("\") AS \"").Append(Constants.RtWellKnownName).Append('"');
        foreach (var (sourceColumn, targets) in resolved)
        {
            foreach (var t in targets)
            {
                sb.Append(", ");
                switch (t.Function)
                {
                    case RollupAggregationColumns.TimeWeightedIntegral:
                        // Σ value × Δt(ms). NULL observations contribute nothing — the signal is
                        // unknown until the next non-NULL observation (concept §3).
                        sb.Append("SUM(CASE WHEN \"").Append(sourceColumn).Append("\" IS NOT NULL THEN \"")
                          .Append(sourceColumn).Append("\" * \"dt_ms\" END)");
                        break;
                    case RollupAggregationColumns.TimeWeightedDuration:
                        sb.Append("SUM(CASE WHEN \"").Append(sourceColumn).Append("\" IS NOT NULL THEN \"dt_ms\" END)");
                        break;
                    default:
                        // Plain aggregations must not see the carry-in virtual row — it lies
                        // outside the bucket. CASE-guard on is_carry keeps their semantics
                        // identical to the standard statement.
                        sb.Append(t.Function).Append("(CASE WHEN NOT \"is_carry\" THEN \"")
                          .Append(sourceColumn).Append("\" END)");
                        break;
                }
                sb.Append(" AS \"").Append(t.ColumnName).Append('"');
            }
        }
        sb.Append(", 0 AS \"").Append(Constants.Generation).Append('"');

        // ---- weighted: interval to the next observation, capped at the bucket end ----
        // Ties on ts (a carry at B_start next to an in-bucket event at exactly B_start) order the
        // carry first (is_carry DESC) so it gets Δt = 0 instead of shadowing the real event.
        sb.AppendLine().AppendLine("FROM (")
          .Append("    SELECT \"").Append(Constants.RtId).Append("\", \"").Append(Constants.RtWellKnownName).Append("\", \"is_carry\"");
        if (sourceColumns.Count > 0)
        {
            sb.Append(", ").Append(sourceColumnList);
        }
        sb.Append(",\n           COALESCE(LEAD(\"ts\") OVER (PARTITION BY \"").Append(Constants.RtId)
          .Append("\" ORDER BY \"ts\", \"is_carry\" DESC), '").Append(bucketEndLiteral)
          .AppendLine("'::timestamp)::bigint - \"ts\"::bigint AS \"dt_ms\"")
          .AppendLine("    FROM (")
          // ---- carry-in: latest observation before the bucket, per rtId, bounded lookback ----
          .Append("        SELECT \"").Append(Constants.RtId).Append("\", \"").Append(Constants.RtWellKnownName)
          .Append("\", '").Append(bucketStartLiteral).Append("'::timestamp AS \"ts\", TRUE AS \"is_carry\"");
        if (sourceColumns.Count > 0)
        {
            sb.Append(", ").Append(sourceColumnList);
        }
        sb.AppendLine()
          .AppendLine("        FROM (")
          .Append("            SELECT \"").Append(Constants.RtId).Append("\", \"").Append(Constants.RtWellKnownName)
          .Append("\", \"").Append(Constants.Timestamp).Append('"');
        if (sourceColumns.Count > 0)
        {
            sb.Append(", ").Append(sourceColumnList);
        }
        sb.Append(",\n                   ROW_NUMBER() OVER (PARTITION BY \"").Append(Constants.RtId)
          .Append("\" ORDER BY \"").Append(Constants.Timestamp).AppendLine("\" DESC) AS \"rn\"")
          .Append("            FROM ").AppendLine(sourceTable)
          .Append("            WHERE \"").Append(Constants.Timestamp).Append("\" < '").Append(bucketStartLiteral)
          .Append("'::timestamp AND \"").Append(Constants.Timestamp).Append("\" >= '").Append(carryFromLiteral)
          .Append("'::timestamp").Append(scopePredicate).AppendLine()
          .AppendLine("        ) \"carry\"")
          .AppendLine("        WHERE \"rn\" = 1")
          .AppendLine("        UNION ALL")
          // ---- in-bucket events ----
          .Append("        SELECT \"").Append(Constants.RtId).Append("\", \"").Append(Constants.RtWellKnownName)
          .Append("\", \"").Append(Constants.Timestamp).Append("\" AS \"ts\", FALSE AS \"is_carry\"");
        if (sourceColumns.Count > 0)
        {
            sb.Append(", ").Append(sourceColumnList);
        }
        sb.AppendLine()
          .Append("        FROM ").AppendLine(sourceTable)
          .Append("        WHERE \"").Append(Constants.Timestamp).Append("\" >= '").Append(bucketStartLiteral)
          .Append("'::timestamp AND \"").Append(Constants.Timestamp).Append("\" < '").Append(bucketEndLiteral)
          .Append("'::timestamp").Append(scopePredicate).AppendLine()
          .AppendLine("    ) \"events\"")
          .AppendLine(") \"weighted\"")
          .Append("GROUP BY \"").Append(Constants.RtId).AppendLine("\"");

        AppendConflictClause(sb, resolved);
        return sb.ToString();
    }

    private static void AppendInsertColumnList(
        StringBuilder sb,
        string targetTable,
        IReadOnlyList<(string SourceColumn, IReadOnlyList<RollupTargetColumn> Targets)> resolved,
        bool includeWasUpdated)
    {
        sb.Append("INSERT INTO ").Append(targetTable).Append(" (")
          .Append('"').Append(Constants.WindowStart).Append("\", ")
          .Append('"').Append(Constants.WindowEnd).Append("\", ")
          .Append('"').Append(Constants.RtId).Append("\", ")
          .Append('"').Append(Constants.CkTypeId).Append("\", ")
          .Append('"').Append(Constants.RtWellKnownName).Append('"');
        if (includeWasUpdated)
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
    }

    private static void AppendConflictClause(
        StringBuilder sb,
        IReadOnlyList<(string SourceColumn, IReadOnlyList<RollupTargetColumn> Targets)> resolved)
    {
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
    }

    /// <summary>Escapes a single-quoted SQL string literal by doubling embedded single-quotes.</summary>
    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
