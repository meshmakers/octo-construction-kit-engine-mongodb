using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Builds the staging + swap SQL for an optimistic rollup recompute (AB#4184, Phase 3c). The
/// recompute aggregates the range into a per-archive staging table (created with the live table's
/// windowed shape) and then replaces the live range from staging. Only CrateDB primitives already
/// used elsewhere in this layer are emitted (<c>DROP TABLE IF EXISTS</c>, <c>DELETE FROM</c>,
/// <c>INSERT INTO … SELECT</c>); the windowed <c>CREATE TABLE</c> is reused from
/// <see cref="ArchiveDdlGenerator"/>.
/// </summary>
/// <remarks>
/// Range bounds are emitted as epoch milliseconds rather than timestamp string literals — CrateDB
/// accepts a numeric value for a <c>TIMESTAMP WITH TIME ZONE</c> column unambiguously, sidestepping
/// any locale / timezone parsing of a string literal.
/// </remarks>
internal static class RollupRecomputeSqlBuilder
{
    /// <summary>
    /// Per-archive staging table identifier in the tenant schema, e.g.
    /// <c>"tenant"."archive_&lt;rollupRtId&gt;__rc"</c>. Deterministic (one per archive): the coalesce
    /// policy guarantees at most one active recompute per archive, so a stable name is safe and lets
    /// a crashed run's leftover table be dropped on the next attempt.
    /// </summary>
    public static string StagingTable(string tenantId, string rollupRtId) =>
        $"\"{TenantSchema.SchemaName(tenantId)}\".\"archive_{rollupRtId}__rc\"";

    /// <summary>Builds <c>DROP TABLE IF EXISTS {table};</c> for idempotent staging cleanup.</summary>
    public static string BuildDropIfExists(string qualifiedTable) =>
        $"DROP TABLE IF EXISTS {qualifiedTable};";

    /// <summary>
    /// Builds the generation-aware staging copy (AB#4184, Phase 6): inserts the staged rows into the
    /// live table stamping every row with <paramref name="generation"/> via a literal in the SELECT
    /// list, instead of copying staging's own generation. The new-generation rows coexist with the
    /// previous generation (the generation column is part of the rollup PK) until the pointer flips
    /// and <see cref="BuildSweepSupersededGenerations"/> removes the old rows. No live DELETE happens
    /// before the pointer flips, so a crash mid-copy leaves readers on the previous generation.
    /// </summary>
    public static string BuildInsertFromStagingWithGeneration(
        string liveTable, string stagingTable, IReadOnlyList<string> columnNames, long generation)
    {
        if (columnNames.Count == 0)
        {
            throw new ArgumentException("columnNames must not be empty", nameof(columnNames));
        }

        var cols = string.Join(", ", columnNames.Select(c => $"\"{c}\""));
        var g = generation.ToString(CultureInfo.InvariantCulture);
        return
            $"INSERT INTO {liveTable} ({cols}, \"{Constants.Generation}\") " +
            $"SELECT {cols}, {g} FROM {stagingTable};";
    }

    /// <summary>
    /// Builds the post-flip sweep: removes the now-superseded rows in <c>[from, to)</c> whose
    /// generation differs from the active one. Runs <b>after</b> the pointer flip, so no consistent
    /// generation is ever removed from under a reader. Optionally scoped to a single
    /// <paramref name="rtIdScope"/> (empty/null = whole range).
    /// </summary>
    public static string BuildSweepSupersededGenerations(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null)
    {
        var g = generation.ToString(CultureInfo.InvariantCulture);
        var scopeClause = string.IsNullOrEmpty(rtIdScope)
            ? string.Empty
            : $" AND \"{Constants.RtId}\" = '{rtIdScope.Replace("'", "''")}'";
        return
            $"DELETE FROM {liveTable} WHERE \"{Constants.WindowStart}\" >= {ToEpochMs(from)} " +
            $"AND \"{Constants.WindowStart}\" < {ToEpochMs(to)} " +
            $"AND \"{Constants.Generation}\" != {g}{scopeClause};";
    }

    /// <summary>
    /// Builds the delete of all recomputed (generation &gt; 0) rows at or after
    /// <paramref name="fromBucketEnd"/>. Companion to <see cref="GenerationMapSqlBuilder.BuildDeleteGenerationsFrom"/>
    /// for a watermark rewind (AB#4184, Phase 6): once the generation pointers are gone the forward
    /// re-aggregation rewrites these windows at generation 0, so the stale higher-generation rows are
    /// removed here. Generation-0 rows are kept (they are the re-aggregation target / steady state).
    /// </summary>
    public static string BuildDeleteRecomputedRowsFrom(string liveTable, DateTime fromBucketEnd) =>
        $"DELETE FROM {liveTable} WHERE \"{Constants.WindowStart}\" >= {ToEpochMs(fromBucketEnd)} " +
        $"AND \"{Constants.Generation}\" != 0;";

    private static string ToEpochMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
}
