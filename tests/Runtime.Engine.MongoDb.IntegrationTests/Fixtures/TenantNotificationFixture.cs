using System.Collections.Concurrent;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     A system tenant whose tenant notifications are recorded: for every tenant-created notification the fixture
///     records whether the tenant was already registered when it was published (what a service that sets the tenant
///     up on that event finds). Its own collection because it replaces the notifications of its provider.
/// </summary>
public class TenantNotificationFixture : SystemFixture
{
    public TenantNotificationFixture()
    {
        Notifications = new RecordingTenantNotifications(() => GetSystemContext());
        Services.AddSingleton<ILoggerProvider>(Logs);
        // Registered after AddRuntimeEngine's TryAdd default: the last registration wins.
        Services.AddSingleton<ITenantNotifications>(Notifications);
    }

    public RecordingTenantNotifications Notifications { get; }

    public CapturingLoggerProvider Logs { get; } = new();
}

/// <summary>Records the tenant-created notifications; can be told to fail the next ones.</summary>
public sealed class RecordingTenantNotifications(Func<Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext> systemContext)
    : ITenantNotifications
{
    private readonly ConcurrentQueue<(string TenantId, bool Registered)> _posCreated = new();
    private readonly ConcurrentQueue<(string Kind, string TenantId)> _published = new();
    private int _failuresLeft;

    /// <summary>The published tenant-created notifications: tenant and whether it was registered at that moment.</summary>
    public IReadOnlyList<(string TenantId, bool Registered)> PosCreated => _posCreated.ToList();

    private int _posCreateAttempts;

    /// <summary>Attempts to publish, failed ones included.</summary>
    public int PosCreateAttempts => Volatile.Read(ref _posCreateAttempts);

    /// <summary>Makes the next <paramref name="count" /> tenant-created publishes throw.</summary>
    public void FailNext(int count) => Interlocked.Exchange(ref _failuresLeft, count);

    /// <summary>Every published post notification of a tenant, in publish order (<c>Create</c>, <c>Update</c>).</summary>
    public IReadOnlyList<string> PublishedFor(string tenantId) => _published
        .Where(p => string.Equals(p.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Kind).ToList();

    public IReadOnlyList<(string TenantId, bool Registered)> PosCreatedFor(string tenantId) =>
        PosCreated.Where(p => string.Equals(p.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId)
    {
        Interlocked.Increment(ref _posCreateAttempts);
        if (Interlocked.Decrement(ref _failuresLeft) >= 0)
        {
            throw new InvalidOperationException("Message bus unavailable (test)");
        }

        Interlocked.Exchange(ref _failuresLeft, 0);
        _posCreated.Enqueue((tenantId, await systemContext().IsTenantRegisteredAsync(tenantId)));
        _published.Enqueue(("Create", tenantId));
    }

    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        _published.Enqueue(("Update", tenantId));
        return Task.CompletedTask;
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
}
