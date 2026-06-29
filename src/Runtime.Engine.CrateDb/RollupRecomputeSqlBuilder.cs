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
    /// Builds the live-range delete: removes the buckets whose <c>window_start</c> falls in
    /// <c>[from, to)</c>, the half-open range the recompute is about to replace from staging.
    /// </summary>
    public static string BuildRangeDelete(string liveTable, DateTime from, DateTime to) =>
        $"DELETE FROM {liveTable} WHERE \"{Constants.WindowStart}\" >= {ToEpochMs(from)} " +
        $"AND \"{Constants.WindowStart}\" < {ToEpochMs(to)};";

    /// <summary>
    /// Builds <c>INSERT INTO {live} (cols) SELECT cols FROM {staging};</c> with an explicit, identical
    /// column list on both sides so the copy is order-independent of how either table was created.
    /// </summary>
    public static string BuildInsertFromStaging(
        string liveTable, string stagingTable, IReadOnlyList<string> columnNames)
    {
        if (columnNames.Count == 0)
        {
            throw new ArgumentException("columnNames must not be empty", nameof(columnNames));
        }

        var cols = string.Join(", ", columnNames.Select(c => $"\"{c}\""));
        return $"INSERT INTO {liveTable} ({cols}) SELECT {cols} FROM {stagingTable};";
    }

    private static string ToEpochMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
}
