using System.Globalization;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>
/// Removes the tenant-level CrateDB state the restore reconcile feature keeps next to the archive tables (AB#6968): the
/// <c>__restore_reconcile</c> marker and the <c>__rollup_repair</c> queue. The per-archive tables (data, <c>__genmap</c>,
/// <c>__rc</c>, <c>__sweep</c>) are <see cref="ArchiveTableDrop"/>'s.
/// </summary>
/// <remarks>
/// Both tables are keyed by the full tenant id and live in the tenant's schema, which tenants whose ids differ only in <c>-</c> and
/// <c>_</c> share. So the tenant's <b>rows</b> are deleted, never the table first: the table is dropped only when it is empty
/// afterwards (no row of a neighbour is left in it), which leaves a deleted tenant's schema without any table of this feature. A
/// tenant that is re-created under the same id would otherwise inherit a set marker and have its rollups blocked.
/// </remarks>
internal static class TenantStateTableDrop
{
    public static async Task DropAsync(
        IStreamDataDatabaseClient databaseClient, IStreamDataDatabaseManagementClient managementClient, string tenantId,
        RecomputeProcessState? recomputeState, CancellationToken cancellationToken)
    {
        var schema = TenantSchema.SchemaName(tenantId);
        foreach (var tableName in new[] { RestoreReconcileMarker.TableName, RollupRepairQueue.TableName })
        {
            var qualified = $"\"{schema}\".\"{tableName}\"";
            if (await databaseClient.GetCountAsync(tenantId, RollupConsistencySql.BuildTableExists(tenantId, tableName),
                    CrateStatementClass.Maintenance, cancellationToken) == 0)
            {
                continue;
            }

            await databaseClient.ExecuteNonQueryAsync(tenantId, BuildDeleteTenantRows(qualified, tenantId),
                CrateStatementClass.Maintenance, cancellationToken);
            await databaseClient.RefreshArchiveTableAsync(tenantId, qualified, CrateStatementClass.Maintenance, cancellationToken);
            var remaining = await databaseClient.GetCountAsync(tenantId, $"SELECT COUNT(*) FROM {qualified};",
                CrateStatementClass.Maintenance, cancellationToken);
            if (remaining == 0)
            {
                await managementClient.ExecuteDdlAsync(tenantId, ArchiveDdlGenerator.GenerateDropTable(qualified), cancellationToken);
            }
        }

        // The answer this process remembered for the marker (five seconds at most) must not outlive the tenant.
        recomputeState?.RestoreMarkers.TryRemove(
            $"\"{schema}\".\"{RestoreReconcileMarker.TableName}\"|{tenantId}", out _);
    }

    internal static string BuildDeleteTenantRows(string qualifiedTable, string tenantId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"DELETE FROM {qualifiedTable} WHERE \"tenant_id\" = '{tenantId.Replace("'", "''", StringComparison.Ordinal)}';");
}
