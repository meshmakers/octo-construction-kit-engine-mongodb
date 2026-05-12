using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Cross-store consistency check between Mongo (CkArchive entities) and CrateDB (archive tables)
/// per concept §11. Run once per tenant at engine startup; idempotent and read-mostly so
/// repeated execution is safe.
/// </summary>
public interface IArchiveReconciler
{
    /// <summary>
    /// Reconciles the tenant's archive metadata with CrateDB reality. For Activated archives
    /// without a backing table the status is flipped to Failed and a Warning event recorded; for
    /// orphan tables (Crate-side tables with no matching CkArchive entity) a Warning is logged
    /// without dropping anything.
    /// </summary>
    Task ReconcileTenantAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IArchiveReconciler"/> implementation. Pulls archives via
/// <see cref="IArchiveRuntimeStore.EnumerateAsync"/>, lists tables via
/// <see cref="IStreamDataDatabaseManagementClient.ListTablesInTenantSchemaAsync"/>, and reports
/// drift via the logger and the audit trail.
/// </summary>
internal sealed class ArchiveReconciler : IArchiveReconciler
{
    private readonly IArchiveRuntimeStore _store;
    private readonly IStreamDataDatabaseManagementClient _managementClient;
    private readonly IArchiveAuditTrail _audit;
    private readonly ILogger<ArchiveReconciler> _logger;

    public ArchiveReconciler(
        IArchiveRuntimeStore store,
        IStreamDataDatabaseManagementClient managementClient,
        IArchiveAuditTrail audit,
        ILogger<ArchiveReconciler> logger)
    {
        _store = store;
        _managementClient = managementClient;
        _audit = audit;
        _logger = logger;
    }

    public async Task ReconcileTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var existingTables = (await _managementClient.ListTablesInTenantSchemaAsync(tenantId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingTables.Count == 0)
        {
            _logger.LogDebug(
                "Reconciliation for tenant '{TenantId}': no CrateDB tables in tenant schema; nothing to compare.",
                tenantId);
        }

        var seenTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var driftCount = 0;

        await foreach (var snapshot in _store.EnumerateAsync().WithCancellation(cancellationToken))
        {
            // Until full per-archive table routing lands, every archive maps to the same legacy
            // table name. The reconciliation logic stays the same: presence in
            // information_schema.tables under the tenant schema means the storage backing exists.
            // When archive routing wires in, swap this to the per-archive table-name resolver
            // (TenantSchema.QualifiedArchiveTable when introduced).
            const string expectedTable = TenantSchema.LegacyStreamDataTable;
            seenTables.Add(expectedTable);

            if (snapshot.Status != CkArchiveStatus.Activated)
            {
                continue;
            }

            if (existingTables.Contains(expectedTable))
            {
                continue;
            }

            _logger.LogWarning(
                "Reconciliation for tenant '{TenantId}': archive {ArchiveRtId} is Activated in Mongo but its CrateDB table is missing — flipping to Failed.",
                tenantId, snapshot.RtId);

            await _store.SetStatusAsync(snapshot.RtId, CkArchiveStatus.Failed);
            await _audit.RecordTransitionAsync(tenantId, snapshot.RtId, CkArchiveStatus.Activated, CkArchiveStatus.Failed,
                "reconciliation: backing CrateDB table missing");
            driftCount++;
        }

        var orphans = existingTables.Except(seenTables, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var orphan in orphans)
        {
            _logger.LogWarning(
                "Reconciliation for tenant '{TenantId}': CrateDB table '{Table}' has no matching CkArchive entity (orphan). Not auto-dropped.",
                tenantId, orphan);
        }

        _logger.LogInformation(
            "Reconciliation for tenant '{TenantId}' complete. Drift fixed: {Drift}. Orphan tables: {Orphans}.",
            tenantId, driftCount, orphans.Count);
    }
}
