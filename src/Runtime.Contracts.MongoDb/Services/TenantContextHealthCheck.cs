using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
///     Health of the MongoDB access of the service: <c>Unhealthy</c> when MongoDB cannot be read,
///     <c>Degraded</c> while this process resolves a tenant whose database carries the ownership
///     marker of another OctoMesh instance (AB#8796), or while the system-database marker of this instance
///     could not be stamped (AB#8672), otherwise <c>Healthy</c>.
/// </summary>
public class TenantContextHealthCheck : IHealthCheck
{
    private readonly ISystemContext _tenantContext;
    private readonly ITenantOwnershipConflicts? _ownershipConflicts;

    public TenantContextHealthCheck(ISystemContext tenantContext)
        : this(tenantContext, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public TenantContextHealthCheck(ISystemContext tenantContext, ITenantOwnershipConflicts? ownershipConflicts = null)
    {
        _tenantContext = tenantContext;
        _ownershipConflicts = ownershipConflicts;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            _ = await _tenantContext.IsCkModelExistingAsync(new CkModelId("System", "1.0.0"));
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Error while accessing mongodb.");
        }

        var conflicts = _ownershipConflicts?.Current ?? [];
        var markerProblem = _ownershipConflicts?.SystemDatabaseMarkerProblem;
        if (conflicts.Count == 0 && markerProblem == null)
        {
            return HealthCheckResult.Healthy("Mongodb is accessible.");
        }

        var data = new Dictionary<string, object>();
        var reasons = new List<string>();
        if (markerProblem != null)
        {
            data["systemDatabaseMarker"] = markerProblem;
            reasons.Add("The system-database marker could not be stamped: " + markerProblem +
                        ". Other OctoMesh instances on this MongoDB server recognise this system database only while " +
                        "it registers a tenant; the next start of the identity service stamps it again.");
        }

        foreach (var conflict in conflicts)
        {
            data[$"tenant:{conflict.TenantId}"] =
                $"database '{conflict.DatabaseName}' is owned by instance '{conflict.OwnerSystemDatabaseName}'";
        }

        if (conflicts.Count > 0)
        {
            reasons.Add("Tenant databases owned by another OctoMesh instance: " + string.Join("; ",
                conflicts.Select(c =>
                    $"tenant '{c.TenantId}', database '{c.DatabaseName}', owner '{c.OwnerSystemDatabaseName}'")) +
                ". Detach the tenant in one of the two instances.");
        }

        return HealthCheckResult.Degraded(string.Join(" ", reasons), data: data);
    }
}
