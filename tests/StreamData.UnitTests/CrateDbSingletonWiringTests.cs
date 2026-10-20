using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7112: the CrateDb client and its repository factory hold process-wide state (connection pool,
/// tenant-free caches) and must stay singletons. A per-call (Transient/Scoped) registration would
/// not be caught by the tenant-freedom sweep, so this pins the lifetime and the shared instance.
/// </summary>
public class CrateDbSingletonWiringTests
{
    [Fact]
    public async Task AddStreamDataDatabase_RegistersCrateClientAndFactoryAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Collaborators the host provides; faked so the factory can be constructed.
        services.AddSingleton(A.Fake<ICkCacheService>());
        services.AddSingleton(A.Fake<IFormulaEngine>());
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();

        Assert.Equal(ServiceLifetime.Singleton, LifetimeOf(services, typeof(CrateDatabaseClient)));
        Assert.Equal(ServiceLifetime.Singleton, LifetimeOf(services, typeof(IStreamDataRepositoryFactory)));
        Assert.Equal(ServiceLifetime.Singleton, LifetimeOf(services, typeof(IStreamDataDatabaseClient)));
        Assert.Equal(ServiceLifetime.Singleton, LifetimeOf(services, typeof(IStreamDataDatabaseManagementClient)));

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

        var client = provider.GetRequiredService<CrateDatabaseClient>();
        Assert.Same(client, provider.GetRequiredService<IStreamDataDatabaseClient>());
        Assert.Same(client, provider.GetRequiredService<IStreamDataDatabaseManagementClient>());
        Assert.Same(client, provider.GetRequiredService<CrateDatabaseClient>());

        var factory = provider.GetRequiredService<IStreamDataRepositoryFactory>();
        Assert.Same(factory, provider.GetRequiredService<IStreamDataRepositoryFactory>());

        // A scope must see the same instances as the root provider.
        using var scope = provider.CreateScope();
        Assert.Same(client, scope.ServiceProvider.GetRequiredService<CrateDatabaseClient>());
        Assert.Same(factory, scope.ServiceProvider.GetRequiredService<IStreamDataRepositoryFactory>());
    }

    private static ServiceLifetime LifetimeOf(IServiceCollection services, Type serviceType)
    {
        var descriptor = Assert.Single(services, d => d.ServiceType == serviceType);
        return descriptor.Lifetime;
    }

    private sealed class NoOpStreamDataConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public void Configure(string? name, StreamDataConfiguration options)
        {
        }

        public void Configure(StreamDataConfiguration options)
        {
        }
    }
}
