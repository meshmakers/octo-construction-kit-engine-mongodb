using System.Globalization;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>
/// The <b>restore reconcile pending</b> marker of a tenant (AB#6968): set when a restore with archive data has finished
/// and the rollups were not yet validated ok or repaired. While it is set, the reclaim, the forward aggregation and the
/// recompute of the tenant do not run (the gate lives in the host's orchestrator ticks and in
/// <see cref="CrateDbArchiveRecomputeExecutor"/>).
/// </summary>
/// <remarks>
/// <para>
/// Stored as a row of the tenant schema's table <c>__restore_reconcile</c> (key: the tenant id), next to the rollup data it
/// protects and not in the tenant's MongoDB: the marker is set by the restore job right after the archive tables were
/// loaded, and a restore replaces MongoDB wholesale. <b>An absent row, or no table, means not pending</b>, so tenants
/// and backups from before this feature behave unchanged.
/// </para>
/// <para>
/// Tenant ids that differ only in <c>-</c> and <c>_</c> share one CrateDB schema (<see cref="TenantSchema.SchemaName"/>),
/// so the row is keyed by the full tenant id. Every column is written explicitly and none has a default (AB#6647).
/// </para>
/// <para>
/// Reading is one small statement; the hosts ask it on every tick and the executor before every recompute, so the answer is
/// remembered for <see cref="CacheTtl"/> per process (set and clear update it at once). A marker set by another process is
/// seen within that time.
/// </para>
/// </remarks>
internal sealed class RestoreReconcileMarker(
    string tenantId,
    IStreamDataDatabaseClient databaseClient,
    IStreamDataDatabaseManagementClient managementClient,
    RecomputeProcessState state,
    TimeProvider clock,
    ILogger logger)
{
    /// <summary>How long an answer is remembered by this process.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>The marker table of the tenant's schema (unquoted name).</summary>
    public const string TableName = "__restore_reconcile";

    public string QualifiedTable => $"\"{TenantSchema.SchemaName(tenantId)}\".\"{TableName}\"";

    internal static string BuildCreateTable(string qualifiedTable) =>
        $"CREATE TABLE IF NOT EXISTS {qualifiedTable} (" +
        " \"tenant_id\" TEXT NOT NULL," +
        " \"marked_at_ms\" BIGINT NOT NULL," +
        " PRIMARY KEY (\"tenant_id\")) CLUSTERED INTO 1 SHARDS;";

    internal static string BuildUpsert(string qualifiedTable, string tenantId, long markedAtMs) =>
        $"INSERT INTO {qualifiedTable} (\"tenant_id\", \"marked_at_ms\") VALUES ({Literal(tenantId)}, {markedAtMs.ToString(CultureInfo.InvariantCulture)}) " +
        $"ON CONFLICT (\"tenant_id\") DO UPDATE SET \"marked_at_ms\" = {markedAtMs.ToString(CultureInfo.InvariantCulture)};";

    internal static string BuildDelete(string qualifiedTable, string tenantId) =>
        $"DELETE FROM {qualifiedTable} WHERE \"tenant_id\" = {Literal(tenantId)};";

    internal static string BuildCount(string qualifiedTable, string tenantId) =>
        $"SELECT COUNT(*) FROM {qualifiedTable} WHERE \"tenant_id\" = {Literal(tenantId)};";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private string CacheKey => $"{QualifiedTable}|{tenantId}";

    /// <summary>True while the tenant is marked. A tenant without the table is not marked.</summary>
    public async Task<bool> IsPendingAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (state.RestoreMarkers.TryGetValue(CacheKey, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Pending;
        }

        bool pending;
        try
        {
            pending = await databaseClient.GetCountAsync(tenantId, BuildCount(QualifiedTable, tenantId),
                CrateStatementClass.Maintenance, cancellationToken) > 0;
        }
        catch (Exception ex) when (CrateDbStreamDataRepository.IsMissingTableError(ex))
        {
            pending = false; // never marked: the table is created by the first Set
        }

        state.RestoreMarkers[CacheKey] = new RecomputeProcessState.MarkerAnswer(pending, now + CacheTtl);
        return pending;
    }

    /// <summary>Sets or clears the marker; durable, and visible to this process at once.</summary>
    public async Task SetAsync(bool pending, CancellationToken cancellationToken)
    {
        if (pending)
        {
            await managementClient.ExecuteDdlAsync(tenantId, BuildCreateTable(QualifiedTable), cancellationToken);
            await databaseClient.ExecuteNonQueryAsync(tenantId,
                BuildUpsert(QualifiedTable, tenantId, clock.GetUtcNow().ToUnixTimeMilliseconds()),
                CrateStatementClass.Maintenance, cancellationToken);
            await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);
            logger.LogWarning(
                "Tenant {TenantId} is marked restore reconcile pending: reclaim, forward aggregation and recompute of its rollups " +
                "are blocked until the rollups validate ok or a repair has run.", tenantId);
        }
        else
        {
            try
            {
                await databaseClient.ExecuteNonQueryAsync(tenantId, BuildDelete(QualifiedTable, tenantId),
                    CrateStatementClass.Maintenance, cancellationToken);
                await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);
            }
            catch (Exception ex) when (CrateDbStreamDataRepository.IsMissingTableError(ex))
            {
                // Never marked: nothing to clear.
            }

            logger.LogInformation("The restore reconcile marker of tenant {TenantId} is cleared.", tenantId);
        }

        state.RestoreMarkers[CacheKey] = new RecomputeProcessState.MarkerAnswer(pending, clock.GetUtcNow().UtcDateTime + CacheTtl);
    }
}
