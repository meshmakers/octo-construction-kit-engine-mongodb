using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Two OctoMesh instances on ONE MongoDB server (AB#8796): this fixture is instance A, and
///     <see cref="Other" /> is instance B with its own system database on the same server — exactly
///     the test-2 dual instance or two local octo-devstack slots. Each instance has its own service
///     provider, so the cross-instance rules are exercised through the real engine on both sides.
/// </summary>
/// <remarks>
///     Its own fixture (and collection) because its tests write foreign ownership markers and
///     double registrations on purpose; the blast radius stays inside this collection.
/// </remarks>
public class TwoInstanceFixture : SystemFixture
{
    private readonly SystemFixture _other = new();

    public TwoInstanceFixture()
    {
        Services.AddSingleton<ILoggerProvider>(Logs);
    }

    /// <summary>The log messages of instance A.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Instance B, sharing the MongoDB server with this fixture (instance A).</summary>
    public SystemFixture Other => _other;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();
        await _other.InitializeAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        await _other.DisposeAsync();
        await base.DisposeServicesAsync();
    }
}
