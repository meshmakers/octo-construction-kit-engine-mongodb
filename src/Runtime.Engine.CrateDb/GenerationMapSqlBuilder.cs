using System;
using System.Globalization;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Builds the SQL for a rollup archive's per-window <b>active-generation pointer</b> side-table
/// (AB#4184, Phase 6). The concept doc (§4) keeps this pointer in "Mongo metadata"; we deliberately
/// co-locate it with the rollup data in a small CrateDB side-table so the flip is a single-row write
/// in the same store as the rows it governs — no cross-store coordination, no CK-model bump.
/// </summary>
/// <remarks>
/// <para>
/// Layout: one row per recomputed half-open range <c>[range_start, range_end)</c> (epoch ms),
/// optionally scoped to a single <c>rtid_scope</c> (empty string = all rtIds; a per-rtId recompute
/// writes the entity's rtId, and the executor restricts aggregation, pointer and sweep to it). The
/// <c>generation</c> column is the active generation for that range: readers select
/// <c>WHERE generation = active(window)</c> via a CASE built from these rows
/// (<see cref="CrateQueryCompiler"/>), and the rollup table's generation column lets the previous
/// generation linger until the sweep removes it.
/// </para>
/// <para>
/// Generations are a per-archive monotonic counter (<see cref="BuildNextGeneration"/> =
/// <c>MAX(generation)+1</c>); the table is created at rollup activation so the read path can always
/// issue a plain SELECT (empty in the steady state, before any recompute).
/// </para>
/// </remarks>
internal static class GenerationMapSqlBuilder
{
    /// <summary>Empty scope sentinel — a genmap row that applies to every rtId in its range.</summary>
    public const string AllRtIdsScope = "";

    /// <summary>
    /// Per-archive generation-map table identifier in the tenant schema, e.g.
    /// <c>"tenant"."archive_&lt;rollupRtId&gt;__genmap"</c>. One per rollup archive, parallel to the
    /// staging table's <c>__rc</c> suffix.
    /// </summary>
    public static string GenMapTable(string tenantId, string rollupRtId) =>
        $"\"{TenantSchema.SchemaName(tenantId)}\".\"archive_{rollupRtId}__genmap\"";

    /// <summary>
    /// Builds <c>CREATE TABLE IF NOT EXISTS …</c> for the generation-map side-table. Single-shard,
    /// no replicas knob (tiny metadata table). PK is the range tuple so a re-recompute of the same
    /// range upserts its pointer in place.
    /// </summary>
    public static string BuildCreateTable(string genMapTable) =>
        $"CREATE TABLE IF NOT EXISTS {genMapTable} (" +
        $" \"range_start\" BIGINT NOT NULL," +
        $" \"range_end\" BIGINT NOT NULL," +
        $" \"rtid_scope\" TEXT NOT NULL DEFAULT ''," +
        $" \"{Constants.Generation}\" BIGINT NOT NULL," +
        $" PRIMARY KEY (\"range_start\", \"range_end\", \"rtid_scope\")) CLUSTERED INTO 1 SHARDS;";

    /// <summary>Builds <c>DROP TABLE IF EXISTS {genmap};</c> — used when the rollup archive is deleted.</summary>
    public static string BuildDropIfExists(string genMapTable) =>
        $"DROP TABLE IF EXISTS {genMapTable};";

    /// <summary>
    /// Builds the next-generation query: <c>SELECT COALESCE(MAX(generation), 0) + 1 …</c>. Monotonic
    /// per archive; a fresh archive (no rows) yields generation 1 for its first recompute.
    /// </summary>
    public static string BuildNextGeneration(string genMapTable) =>
        $"SELECT COALESCE(MAX(\"{Constants.Generation}\"), 0) + 1 AS next FROM {genMapTable};";

    /// <summary>
    /// Builds the atomic flip: upsert the active generation for <c>[from, to)</c> (scope) to
    /// <paramref name="generation"/>. This single-row write is the commit point — until it lands,
    /// readers keep seeing the previous generation.
    /// </summary>
    public static string BuildUpsertPointer(string genMapTable, DateTime from, DateTime to, string rtIdScope, long generation)
    {
        var scope = (rtIdScope ?? AllRtIdsScope).Replace("'", "''");
        var g = generation.ToString(CultureInfo.InvariantCulture);
        return
            $"INSERT INTO {genMapTable} (\"range_start\", \"range_end\", \"rtid_scope\", \"{Constants.Generation}\") " +
            $"VALUES ({ToEpochMs(from)}, {ToEpochMs(to)}, '{scope}', {g}) " +
            $"ON CONFLICT (\"range_start\", \"range_end\", \"rtid_scope\") DO UPDATE SET \"{Constants.Generation}\" = {g};";
    }

    /// <summary>Builds <c>SELECT range_start, range_end, rtid_scope, generation FROM {genmap};</c>.</summary>
    public static string BuildSelectAll(string genMapTable) =>
        $"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"{Constants.Generation}\" FROM {genMapTable};";

    /// <summary>
    /// Builds the delete of all active-generation entries whose range reaches at or past
    /// <paramref name="fromBucketEnd"/> — i.e. <c>range_end &gt; fromBucketEnd</c>. Used when a rollup
    /// watermark is rewound over a recomputed range (AB#4184, Phase 6): clearing these entries lets
    /// the forward re-aggregation (generation 0) become the active generation again. Entries entirely
    /// before the boundary (not rewound) are kept. An entry that straddles the boundary is removed in
    /// full, so its pre-boundary part also falls back to generation 0 — align rewind boundaries with
    /// recompute-range boundaries to avoid losing the non-rewound part.
    /// </summary>
    public static string BuildDeleteGenerationsFrom(string genMapTable, DateTime fromBucketEnd) =>
        $"DELETE FROM {genMapTable} WHERE \"range_end\" > {ToEpochMs(fromBucketEnd)};";

    /// <summary>
    /// Builds the delete of every pointer entry whose range lies entirely inside <c>[from, to)</c>
    /// other than the entry just flipped (AB#5189). Called right after the flip: the entry just
    /// written covers those ranges at a strictly higher generation, and the post-flip sweep has
    /// already removed the rows they pointed at, so they are dead weight — an entry pointing at a
    /// generation that no longer exists in the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>contained</b>, not overlapping: an entry reaching beyond the flipped range
    /// still governs the part outside it, and dropping it would silently send that part back to
    /// generation 0. The read filter is unaffected either way — it orders by generation descending
    /// and the flipped entry is the highest — so this is table hygiene, not a correctness fix.
    /// </para>
    /// <para>
    /// Scope follows the sweep: an unscoped flip (<paramref name="rtIdScope"/> empty) sweeps the
    /// rows of every entity in the range, so a contained entry of <em>any</em> scope is dead and is
    /// dropped; a scoped flip sweeps one entity's rows only, so only contained entries of that same
    /// scope are.
    /// </para>
    /// </remarks>
    public static string BuildDeleteContainedPointers(
        string genMapTable, DateTime from, DateTime to, string rtIdScope)
    {
        var fromMs = ToEpochMs(from);
        var toMs = ToEpochMs(to);
        var scope = (rtIdScope ?? AllRtIdsScope).Replace("'", "''");
        var scopeClause = scope.Length == 0 ? string.Empty : $" AND \"rtid_scope\" = '{scope}'";
        return
            $"DELETE FROM {genMapTable} WHERE \"range_start\" >= {fromMs} AND \"range_end\" <= {toMs}{scopeClause} " +
            $"AND NOT (\"range_start\" = {fromMs} AND \"range_end\" = {toMs} AND \"rtid_scope\" = '{scope}');";
    }

    private static string ToEpochMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
}
