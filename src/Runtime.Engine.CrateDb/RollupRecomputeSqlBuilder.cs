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
    /// policy guarantees at most one active recompute per archive, so a stable name is safe. The
    /// table is kept between runs and dropped with its archive (<see cref="ArchiveTableDrop"/>).
    /// </summary>
    public static string StagingTable(string tenantId, string rollupRtId) =>
        $"\"{TenantSchema.SchemaName(tenantId)}\".\"archive_{rollupRtId}__rc\"";

    /// <summary>Builds <c>DROP TABLE IF EXISTS {table};</c> for idempotent staging cleanup.</summary>
    public static string BuildDropIfExists(string qualifiedTable) =>
        $"DROP TABLE IF EXISTS {qualifiedTable};";

    /// <summary>
    /// Builds the count of the staged rows whose window starts in <c>[from, to)</c> — what a copy
    /// slice over that range would insert.
    /// </summary>
    public static string BuildCountStaged(string stagingTable, DateTime from, DateTime to) =>
        $"SELECT COUNT(*) FROM {stagingTable} WHERE {WindowStartIn(from, to)};";

    /// <summary>
    /// Builds one slice of the generation-aware staging copy (AB#4184, Phase 6): inserts the staged
    /// rows whose window starts in <c>[from, to)</c> into the live table, stamping every row with
    /// <paramref name="generation"/> via a literal in the SELECT list instead of copying staging's
    /// own generation. The new-generation rows coexist with the previous generation (the generation
    /// column is part of the rollup PK) until the pointer flips and
    /// <see cref="BuildSweepSupersededGenerations"/> removes the old rows. No live DELETE happens
    /// before the pointer flips, so a crash mid-copy leaves readers on the previous generation.
    /// </summary>
    /// <remarks>
    /// The copy runs as several such statements, each bounded by a row count the caller measured
    /// (AB#6283): one statement over a whole chunk grows with the rows of the chunk and has no upper
    /// bound. The slices write disjoint keys of the same generation, and no reader can select that
    /// generation before the pointer names it, so splitting the copy changes nothing a reader sees.
    /// </remarks>
    /// <param name="liveTable">The rollup's live table.</param>
    /// <param name="stagingTable">The staging table the rows are copied from.</param>
    /// <param name="columnNames">The columns copied (all but the generation).</param>
    /// <param name="generation">The generation every copied row is stamped with.</param>
    /// <param name="from">Inclusive start of the slice (window start).</param>
    /// <param name="to">Exclusive end of the slice (window start).</param>
    /// <param name="skipExisting">
    /// Adds <c>ON CONFLICT DO NOTHING</c>: the repeat of a slice CrateDB did not store completely (AB#8680) inserts
    /// only the rows that are missing and leaves the ones the first statement stored alone.
    /// </param>
    public static string BuildInsertFromStagingSlice(
        string liveTable, string stagingTable, IReadOnlyList<string> columnNames, long generation,
        DateTime from, DateTime to, bool skipExisting = false)
    {
        if (columnNames.Count == 0)
        {
            throw new ArgumentException("columnNames must not be empty", nameof(columnNames));
        }

        var cols = string.Join(", ", columnNames.Select(c => $"\"{c}\""));
        var g = generation.ToString(CultureInfo.InvariantCulture);
        return
            $"INSERT INTO {liveTable} ({cols}, \"{Constants.Generation}\") " +
            $"SELECT {cols}, {g} FROM {stagingTable} WHERE {WindowStartIn(from, to)}" +
            (skipExisting ? " ON CONFLICT DO NOTHING;" : ";");
    }

    /// <summary>
    /// Builds the in-place upsert of a set-scoped refresh (AB#9016) for the staged windows that start inside
    /// <c>[from, to)</c>: every staged row is written into the live table at <paramref name="activeGeneration"/> - the
    /// generation the readers select for its window and rtId today (<see cref="QueryBuilder.GenerationFilterSql.RenderActiveGeneration"/>)
    /// - and replaces the row stored there. No new generation, so neither a pointer flip nor a sweep follows.
    /// </summary>
    /// <param name="liveTable">The rollup's live table.</param>
    /// <param name="stagingTable">The staging table holding the refreshed rows (generation 0).</param>
    /// <param name="columnNames">Every column of the staged rows except <c>generation</c>; the key columns among them stay.</param>
    /// <param name="activeGeneration">SQL expression over <c>window_start</c> and <c>rtid</c> yielding the active generation.</param>
    /// <param name="from">Inclusive slice start (window start).</param>
    /// <param name="to">Exclusive slice end (window start).</param>
    public static string BuildUpsertFromStagingAtActiveGenerationSlice(
        string liveTable, string stagingTable, IReadOnlyList<string> columnNames, string activeGeneration,
        DateTime from, DateTime to)
    {
        if (columnNames.Count == 0)
        {
            throw new ArgumentException("columnNames must not be empty", nameof(columnNames));
        }

        var keyColumns = new[] { Constants.WindowStart, Constants.WindowEnd, Constants.RtId, Constants.CkTypeId, Constants.Generation };
        var cols = string.Join(", ", columnNames.Select(c => $"\"{c}\""));
        var updates = string.Join(", ", columnNames
            .Where(c => !keyColumns.Contains(c, StringComparer.Ordinal))
            .Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));
        return
            $"INSERT INTO {liveTable} ({cols}, \"{Constants.Generation}\") " +
            $"SELECT {cols}, {activeGeneration} FROM {stagingTable} WHERE {WindowStartIn(from, to)} " +
            $"ON CONFLICT ({string.Join(", ", keyColumns.Select(c => $"\"{c}\""))}) DO UPDATE SET {updates};";
    }

    /// <summary>
    /// Builds the read of the refresh marker of a set-scoped refresh (AB#9016): the <c>rtchangeddatetime</c> of the oldest
    /// staged row, in epoch milliseconds (<c>NULL</c> when nothing was staged).
    /// </summary>
    /// <remarks>
    /// A staged row gets <c>rtchangeddatetime</c> = the time it was staged (column default) and the in-place upsert copies it.
    /// A live row of the scoped series that the upsert did not reach still carries the time it was written before - by an
    /// earlier run, which finished before this run staged anything - so it is older than the marker. The staging table holds
    /// only this run's rows (it is emptied before every run).
    /// </remarks>
    public static string BuildReadRefreshMarker(string stagingTable) =>
        $"SELECT CAST(MIN(\"{Constants.RtChangedDateTime}\") AS BIGINT) AS \"marker\" FROM {stagingTable};";

    /// <summary>
    /// Builds the count of the live rows a set-scoped refresh (AB#9016) has written in <c>[from, to)</c>: rows of the scoped
    /// entities at the generation the readers select (<paramref name="activeGenerationPredicate"/>) that are at least as
    /// new as the refresh marker (<see cref="BuildReadRefreshMarker"/>). The in-place upsert mostly replaces rows that
    /// existed before, so a plain count would not show a slice CrateDB stored only in part (AB#8680).
    /// </summary>
    public static string BuildCountRefreshedInScope(
        string liveTable, DateTime from, DateTime to, string scopeCondition, string activeGenerationPredicate, long markerMs) =>
        $"SELECT COUNT(*) FROM {liveTable} WHERE {WindowStartIn(from, to)} AND {scopeCondition} AND {activeGenerationPredicate} " +
        $"AND {MarkerCondition(markerMs, refreshed: true)};";

    /// <summary>
    /// Builds the count (<paramref name="delete"/> false) or the delete (true) of the live rows of the scoped entities in
    /// <c>[from, to)</c> at the generation the readers select that the refresh did not write (AB#9016): older than the refresh
    /// marker, or every such row when nothing was staged (<paramref name="markerMs"/> null). These are buckets whose source
    /// rows are gone; a full recompute leaves them out of the new generation, so the refresh removes them to stay
    /// equivalent. Bounded by series × buckets of the refresh, like the upsert.
    /// </summary>
    public static string BuildStaleInScope(
        string liveTable, DateTime from, DateTime to, string scopeCondition, string activeGenerationPredicate, long? markerMs,
        bool delete)
    {
        var where = $"WHERE {WindowStartIn(from, to)} AND {scopeCondition} AND {activeGenerationPredicate}" +
                    (markerMs is { } marker ? $" AND {MarkerCondition(marker, refreshed: false)}" : string.Empty);
        return delete ? $"DELETE FROM {liveTable} {where};" : $"SELECT COUNT(*) FROM {liveTable} {where};";
    }

    // A row without rtchangeddatetime (written before the column had its default) was not written by this refresh.
    private static string MarkerCondition(long markerMs, bool refreshed) => refreshed
        ? $"\"{Constants.RtChangedDateTime}\" >= {markerMs.ToString(CultureInfo.InvariantCulture)}"
        : $"(\"{Constants.RtChangedDateTime}\" < {markerMs.ToString(CultureInfo.InvariantCulture)} OR \"{Constants.RtChangedDateTime}\" IS NULL)";

    /// <summary>Builds the count of every row in the staging table.</summary>
    public static string BuildCountAllStaged(string stagingTable) =>
        $"SELECT COUNT(*) FROM {stagingTable};";

    /// <summary>Builds <c>DELETE FROM {staging};</c> — empties a staging table that is kept.</summary>
    public static string BuildDeleteAllStaged(string stagingTable) =>
        $"DELETE FROM {stagingTable};";

    /// <summary>
    /// Builds the delete of one bucket's staged rows (all entities) — used when a bucket has to be
    /// aggregated again because its source committed a recompute while it was being read.
    /// </summary>
    public static string BuildDeleteStagedBucket(string stagingTable, DateTime bucketStart) =>
        $"DELETE FROM {stagingTable} WHERE \"{Constants.WindowStart}\" = {ToEpochMs(bucketStart)};";

    /// <summary>
    /// Builds the delete of one bucket's forward-aggregated (generation 0) rows in a rollup's live
    /// table — used when a forward aggregation of the bucket is discarded because its source
    /// committed a recompute while it was being read. Recomputed generations are left alone.
    /// </summary>
    public static string BuildDeleteForwardBucket(string liveTable, DateTime bucketStart) =>
        $"DELETE FROM {liveTable} WHERE \"{Constants.WindowStart}\" = {ToEpochMs(bucketStart)} " +
        $"AND \"{Constants.Generation}\" = 0;";

    /// <summary>
    /// Builds the post-flip sweep: removes the now-superseded rows in <c>[from, to)</c> whose
    /// generation differs from the active one. Runs <b>after</b> the pointer flip, so no consistent
    /// generation is ever removed from under a reader. Optionally scoped to a single
    /// <paramref name="rtIdScope"/> (empty/null = whole range). The executor calls it once per
    /// row-counted slice of the recomputed range (<see cref="BuildCountSuperseded"/>).
    /// </summary>
    public static string BuildSweepSupersededGenerations(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null)
    {
        var g = generation.ToString(CultureInfo.InvariantCulture);
        return
            $"DELETE FROM {liveTable} WHERE \"{Constants.WindowStart}\" >= {ToEpochMs(from)} " +
            $"AND \"{Constants.WindowStart}\" < {ToEpochMs(to)} " +
            $"AND \"{Constants.Generation}\" != {g}{ScopeClause(rtIdScope)};";
    }

    /// <summary>
    /// Builds the count of the rows <see cref="BuildSweepSupersededGenerations"/> would delete for
    /// the same arguments.
    /// </summary>
    public static string BuildCountSuperseded(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null) =>
        $"SELECT COUNT(*) FROM {liveTable} WHERE {WindowStartIn(from, to)} " +
        $"AND \"{Constants.Generation}\" != {generation.ToString(CultureInfo.InvariantCulture)}{ScopeClause(rtIdScope)};";

    /// <summary>
    /// Builds the count of the rows of exactly <paramref name="generation"/> in <c>[from, to)</c>
    /// (within the scope when set).
    /// </summary>
    public static string BuildCountGeneration(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null) =>
        $"SELECT COUNT(*) FROM {liveTable} WHERE {WindowStartIn(from, to)} " +
        $"AND \"{Constants.Generation}\" = {generation.ToString(CultureInfo.InvariantCulture)}{ScopeClause(rtIdScope)};";

    /// <summary>
    /// Builds the delete of the rows of exactly <paramref name="generation"/> in <c>[from, to)</c>
    /// (within the scope when set) — the rows a copy that never committed left under the generation
    /// number the next run is handed again.
    /// </summary>
    public static string BuildDeleteGenerationSlice(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null) =>
        $"DELETE FROM {liveTable} WHERE {WindowStartIn(from, to)} " +
        $"AND \"{Constants.Generation}\" = {generation.ToString(CultureInfo.InvariantCulture)}{ScopeClause(rtIdScope)};";

    /// <summary>
    /// Builds the count of the rows in <c>[from, to)</c> (within the scope when set) whose
    /// generation is <b>below</b> <paramref name="generation"/>.
    /// </summary>
    public static string BuildCountBelowGeneration(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null) =>
        $"SELECT COUNT(*) FROM {liveTable} WHERE {WindowStartIn(from, to)} " +
        $"AND \"{Constants.Generation}\" < {generation.ToString(CultureInfo.InvariantCulture)}{ScopeClause(rtIdScope)};";

    /// <summary>
    /// Builds the delete of the rows in <c>[from, to)</c> (within the scope when set) whose
    /// generation is <b>below</b> <paramref name="generation"/> — the reclaim of what a sweep left
    /// behind. Given a pointer entry for the range at that generation, every reader selects a
    /// generation at or above it there, so those rows are dead. Strictly below, never "different
    /// from": the rows of a newer recompute overlapping the range lie above it and must stay.
    /// </summary>
    public static string BuildDeleteBelowGenerationSlice(
        string liveTable, DateTime from, DateTime to, long generation, string? rtIdScope = null) =>
        $"DELETE FROM {liveTable} WHERE {WindowStartIn(from, to)} " +
        $"AND \"{Constants.Generation}\" < {generation.ToString(CultureInfo.InvariantCulture)}{ScopeClause(rtIdScope)};";

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

    private static string WindowStartIn(DateTime from, DateTime to) =>
        $"\"{Constants.WindowStart}\" >= {ToEpochMs(from)} AND \"{Constants.WindowStart}\" < {ToEpochMs(to)}";

    private static string ScopeClause(string? rtIdScope) =>
        string.IsNullOrEmpty(rtIdScope)
            ? string.Empty
            : $" AND \"{Constants.RtId}\" = '{rtIdScope.Replace("'", "''")}'";

    private static string ToEpochMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
}
