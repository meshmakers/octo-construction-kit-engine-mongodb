using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

public class TenantContextHealthCheck(ISystemContext tenantContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            _ = await tenantContext.IsCkModelExistingAsync(new CkModelId("System", "1.0.0"));
            return HealthCheckResult.Healthy("Mongodb is accessible.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Error while accessing mongodb.");
        }
    }
}