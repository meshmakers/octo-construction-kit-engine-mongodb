using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;

public static class HealthCheckBuilderExtensions
{
    private static readonly string[] MongodbTags = ["mongodb"];

    public static IHealthChecksBuilder AddSystemContextHealthCheck(this IHealthChecksBuilder builder)
    {
        builder.AddCheck<TenantContextHealthCheck>(
            "TenantContext", 
            HealthStatus.Unhealthy, 
            MongodbTags);
        return builder;
    }
}