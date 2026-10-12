using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;

/// <summary>
/// Extension methods for registering stream data database services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the CrateDB stream data database client and related services. Mirrors the
    /// runtime-engine-builder variant <c>AddCrateDbStreamDataRepository&lt;T&gt;</c> but is callable
    /// directly on an <see cref="IServiceCollection"/> for hosts that compose their own DI graph
    /// (integration test fixtures, sample apps, etc.).
    /// </summary>
    public static IServiceCollection AddStreamDataDatabase<TConfigureOptions>(this IServiceCollection services)
        where TConfigureOptions : class, IConfigureNamedOptions<StreamDataConfiguration>
    {
        services.ConfigureOptions(typeof(TConfigureOptions));

        // AB#6283: slice size and command timeout of the recompute / rollup aggregation statements.
        // A host composed through this method need not have an IConfiguration; bind the section
        // when there is one, keep the defaults (or the host's own Configure) otherwise.
        services.AddOptions<CrateRecomputeOptions>()
            .Configure<IServiceProvider>((options, provider) =>
                provider.GetService<IConfiguration>()?.GetSection(CrateRecomputeOptions.SectionName).Bind(options))
            .Validate(o => o.IsValid(), CrateRecomputeOptions.ValidationMessage);

        // Register CrateDatabaseClient as singleton, exposed via multiple interfaces
        services.AddSingleton<CrateDatabaseClient>();
        services.AddSingleton<IStreamDataDatabaseClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        services.AddSingleton<IStreamDataDatabaseManagementClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        services.AddSingleton<IStreamDataHealthCheckClient>(p => p.GetRequiredService<CrateDatabaseClient>());

        services.AddSingleton<ICrateDbConnectionAccess, CrateDbConnectionAccess>();

        // Register the per-tenant repository factory. Without this `TenantContext.GetStreamDataRepository`
        // returns null and `IArchiveLifecycleService` is unavailable — both invariants production
        // callers depend on. Keeps this extension symmetric with `AddCrateDbStreamDataRepository`.
        services.AddSingleton<IStreamDataRepositoryFactory, CrateDbStreamDataRepositoryFactory>();

        // AB#5157: process-wide archive coverage memo (also exposed as IArchiveCoverageInvalidator),
        // registered here too so both entry points wire the same coverage cache.
        services.AddArchiveCoverageCache();

        return services;
    }
}
