using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;

/// <summary>
/// Extension methods for registering stream data database services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the CrateDB stream data database client and related services.
    /// </summary>
    public static IServiceCollection AddStreamDataDatabase<TConfigureOptions>(this IServiceCollection services)
        where TConfigureOptions : IConfigureNamedOptions<StreamDataConfiguration>
    {
        services.ConfigureOptions(typeof(TConfigureOptions));

        // Register CrateDatabaseClient as singleton, exposed via multiple interfaces
        services.AddSingleton<CrateDatabaseClient>();
        services.AddSingleton<IStreamDataDatabaseClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        services.AddSingleton<IStreamDataDatabaseManagementClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        services.AddSingleton<IStreamDataHealthCheckClient>(p => p.GetRequiredService<CrateDatabaseClient>());

        services.AddSingleton<ICrateDbConnectionAccess, CrateDbConnectionAccess>();

        return services;
    }
}
