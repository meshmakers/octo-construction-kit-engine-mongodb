using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

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
        // Registered after AddRuntimeEngine's TryAdd default: the last registration wins.
        Services.AddSingleton<ITenantNotifications>(Notifications);
    }

    /// <summary>
    ///     The tenant notifications of instance A, with a hook that runs inside an attach between the namespace
    ///     check and the ownership claim (AB#8796 concurrent-attach test).
    /// </summary>
    public HookableTenantNotifications Notifications { get; } = new();

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

/// <summary>No-op tenant notifications with an optional hook on <see cref="NotifyPreTenantCreateAsync" />.</summary>
public sealed class HookableTenantNotifications : ITenantNotifications
{
    /// <summary>Runs on every pre-create notification (create and attach) while set; null by default.</summary>
    public Func<string, Task>? OnPreTenantCreate { get; set; }

    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId) =>
        OnPreTenantCreate?.Invoke(tenantId) ?? Task.CompletedTask;

    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
}
