using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     Process-wide record of the ownership conflicts seen on tenant resolve (AB#8796), keyed by
///     normalized tenant id, and of a failed system-database marker stamp (AB#8672). Feeds the service health
///     check (<c>Degraded</c>) and decides that the resolve warning is logged once per process and tenant.
/// </summary>
internal sealed class TenantOwnershipConflictTracker : ITenantOwnershipConflicts
{
    private readonly ConcurrentDictionary<string, TenantOwnershipConflict> _conflicts =
        new(StringComparer.Ordinal);

    public IReadOnlyList<TenantOwnershipConflict> Current =>
        _conflicts.Values.OrderBy(c => c.TenantId, StringComparer.Ordinal).ToList();

    /// <summary>
    ///     Records a conflict. Returns true when it is new for this process (or names another owner or
    ///     database than the recorded one), so the caller logs the warning exactly then.
    /// </summary>
    public bool Report(string tenantId, string databaseName, string ownerSystemDatabaseName)
    {
        var conflict = new TenantOwnershipConflict(tenantId, databaseName, ownerSystemDatabaseName, DateTime.UtcNow);
        var isNew = true;
        _conflicts.AddOrUpdate(tenantId, conflict, (_, existing) =>
        {
            if (existing.DatabaseName == databaseName && existing.OwnerSystemDatabaseName == ownerSystemDatabaseName)
            {
                isNew = false;
                return existing;
            }

            return conflict;
        });
        return isNew;
    }

    /// <summary>Forgets the conflict of a tenant (resolved without conflict, detached or deleted).</summary>
    public void Clear(string tenantId) => _conflicts.TryRemove(tenantId, out _);

    private volatile string? _systemDatabaseMarkerProblem;

    /// <inheritdoc />
    public string? SystemDatabaseMarkerProblem => _systemDatabaseMarkerProblem;

    /// <summary>Records why the system-database marker could not be stamped (AB#8672).</summary>
    public void ReportSystemDatabaseMarkerProblem(string reason) => _systemDatabaseMarkerProblem = reason;

    /// <summary>The system-database marker is stamped: forgets an earlier failure.</summary>
    public void ClearSystemDatabaseMarkerProblem() => _systemDatabaseMarkerProblem = null;
}
