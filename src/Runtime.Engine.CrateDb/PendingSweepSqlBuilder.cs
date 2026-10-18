using System.Globalization;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Builds the SQL for a rollup's <b>pending-sweep record</b> table (AB#6968, rollup recompute concept "Sweep grace
/// period"): the durable list of recomputed ranges whose superseded generation has not been removed yet.
/// </summary>
/// <remarks>
/// <para>
/// A recompute flips a range to generation N+1 and used to remove the rows of the older generations at once. With a
/// sweep grace period above zero it records the sweep here instead and the removal runs when the flip time plus the
/// grace period has passed — see <see cref="CrateDbArchiveRecomputeExecutor"/>. The record is a CrateDB row next to the
/// data (not process memory), so a restart between the flip and the sweep does not lose it.
/// </para>
/// <para>
/// One row per flipped pointer entry <c>(range_start, range_end, rtid_scope, generation)</c> with the epoch
/// millisecond of the flip. <b>Every column is written explicitly</b> by every statement and none has a column DEFAULT:
/// CrateDB before 5.10.12 drops deterministic defaults on the replica copy of rows an
/// <c>INSERT … ON CONFLICT DO UPDATE</c> creates (AB#6647), so a column that relied on one would read NULL there.
/// </para>
/// </remarks>
internal static class PendingSweepSqlBuilder
{
    /// <summary>The record table of a rollup in the tenant schema, e.g. <c>"tenant"."archive_&lt;rtId&gt;__sweep"</c>.</summary>
    public static string SweepTable(string tenantId, string rollupRtId) =>
        $"\"{TenantSchema.SchemaName(tenantId)}\".\"{SweepTableName(rollupRtId)}\"";

    /// <summary>The unquoted table name.</summary>
    public static string SweepTableName(string rollupRtId) => $"archive_{rollupRtId}__sweep";

    /// <summary><c>CREATE TABLE IF NOT EXISTS …</c>, one shard, no column defaults.</summary>
    public static string BuildCreateTable(string sweepTable) =>
        $"CREATE TABLE IF NOT EXISTS {sweepTable} (" +
        " \"range_start\" BIGINT NOT NULL," +
        " \"range_end\" BIGINT NOT NULL," +
        " \"rtid_scope\" TEXT NOT NULL," +
        $" \"{Constants.Generation}\" BIGINT NOT NULL," +
        " \"flipped_at_ms\" BIGINT NOT NULL," +
        $" PRIMARY KEY (\"range_start\", \"range_end\", \"rtid_scope\", \"{Constants.Generation}\")) CLUSTERED INTO 1 SHARDS;";

    /// <summary>Records (or renews) the sweep of <paramref name="entry"/>; every column is named.</summary>
    public static string BuildUpsert(string sweepTable, GenerationRange entry, long flippedAtMs)
    {
        var flipped = flippedAtMs.ToString(CultureInfo.InvariantCulture);
        return
            $"INSERT INTO {sweepTable} (\"range_start\", \"range_end\", \"rtid_scope\", \"{Constants.Generation}\", \"flipped_at_ms\") " +
            $"VALUES ({Number(entry.StartMs)}, {Number(entry.EndMs)}, {Text(entry.Scope)}, {Number(entry.Generation)}, {flipped}) " +
            $"ON CONFLICT (\"range_start\", \"range_end\", \"rtid_scope\", \"{Constants.Generation}\") " +
            $"DO UPDATE SET \"flipped_at_ms\" = {flipped};";
    }

    /// <summary>Every record of the table.</summary>
    public static string BuildSelectAll(string sweepTable) =>
        $"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"{Constants.Generation}\", \"flipped_at_ms\" FROM {sweepTable};";

    /// <summary>Removes the record of <paramref name="entry"/> once its sweep is done (or obsolete).</summary>
    public static string BuildDelete(string sweepTable, GenerationRange entry) =>
        $"DELETE FROM {sweepTable} WHERE \"range_start\" = {Number(entry.StartMs)} AND \"range_end\" = {Number(entry.EndMs)} " +
        $"AND \"rtid_scope\" = {Text(entry.Scope)} AND \"{Constants.Generation}\" = {Number(entry.Generation)};";

    /// <summary>1 when the table exists in the schema, else 0 (a count, so the maintenance statement budget applies).</summary>
    public static string BuildExists(string tenantId, string rollupRtId) =>
        "SELECT COUNT(*) FROM information_schema.tables " +
        $"WHERE table_schema = '{TenantSchema.SchemaName(tenantId)}' AND table_name = '{SweepTableName(rollupRtId)}';";

    /// <summary>
    /// The names of the record tables in the tenant schema (one query for all rollups of the tenant). A schema can be shared
    /// by tenants whose ids differ only in <c>-</c> and <c>_</c>, so the caller intersects the names with its own rollups.
    /// </summary>
    public static string BuildListSweepTables(string tenantId) =>
        "SELECT table_name FROM information_schema.tables " +
        $"WHERE table_schema = '{TenantSchema.SchemaName(tenantId)}' AND table_name LIKE 'archive%__sweep';";

    /// <summary><c>DROP TABLE IF EXISTS</c>, used when the rollup is deleted.</summary>
    public static string BuildDropIfExists(string sweepTable) => $"DROP TABLE IF EXISTS {sweepTable};";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(string value) => "'" + (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";
}
