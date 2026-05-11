using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions for registering CrateDB-based stream data services in the runtime engine.
/// </summary>
public static class StreamDataEngineBuilderExtensions
{
    /// <summary>
    /// Adds CrateDB stream data services to the runtime engine builder.
    /// Registers the database client, connection access, and the CrateDbStreamDataRepository.
    /// The caller must also register an IConfigureNamedOptions&lt;StreamDataConfiguration&gt;
    /// implementation to provide the connection string.
    /// </summary>
    /// <typeparam name="TConfigureOptions">A class implementing IConfigureNamedOptions&lt;StreamDataConfiguration&gt;</typeparam>
    /// <param name="builder">The runtime engine builder</param>
    /// <returns>The builder for chaining</returns>
    public static IRuntimeEngineBuilder AddCrateDbStreamDataRepository<TConfigureOptions>(
        this IRuntimeEngineBuilder builder)
        where TConfigureOptions : class, IConfigureNamedOptions<StreamDataConfiguration>
    {
        builder.Services.ConfigureOptions(typeof(TConfigureOptions));

        // Bind the instance-level kill switch (concept §5). Default value
        // (StreamDataInstanceConfiguration.Enabled = false) keeps the feature opt-in: tenants
        // can only call EnableStreamDataAsync once StreamData:Enabled is set to true in
        // appsettings.
        builder.Services.AddOptions<StreamDataInstanceConfiguration>()
            .BindConfiguration(StreamDataInstanceConfiguration.SectionName);

        // Register CrateDatabaseClient as singleton, exposed via multiple interfaces
        builder.Services.AddSingleton<CrateDatabaseClient>();
        builder.Services.AddSingleton<IStreamDataDatabaseClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        builder.Services.AddSingleton<IStreamDataDatabaseManagementClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        builder.Services.AddSingleton<IStreamDataHealthCheckClient>(p => p.GetRequiredService<CrateDatabaseClient>());

        builder.Services.AddSingleton<ICrateDbConnectionAccess, CrateDbConnectionAccess>();

        builder.Services.AddSingleton<IStreamDataRepositoryFactory, CrateDbStreamDataRepositoryFactory>();

        return builder;
    }
}
