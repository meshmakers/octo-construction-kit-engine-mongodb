using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     Lets an engine operation that owns its transaction publish the tenant-created notification itself, after its
///     commit, so a failed publish reaches that operation instead of being logged only.
/// </summary>
/// <remarks>
///     A create or attach registers the tenant in the caller's transaction. Other services set the tenant up when they
///     receive the notification, so it must not be published before the registration is committed. By default a
///     create or attach inside a transaction publishes after the commit (session callback, a few attempts, then a
///     warning). An engine operation that starts and commits the transaction itself (the restore) runs the create or
///     attach inside <see cref="Begin" /> with its session: the notification of a create or attach in exactly that
///     session is collected here, and the operation calls <see cref="PublishAsync" /> after its commit, where a failure is
///     reported to its own caller. Any other session in the same async flow is not affected. The scope flows with the
///     async call (<see cref="AsyncLocal{T}" />); nothing is published when the operation never calls
///     <see cref="PublishAsync" /> (a failed create or attach, an aborted transaction).
/// </remarks>
internal sealed class TenantCreatedNotificationScope : IDisposable
{
    private static readonly AsyncLocal<TenantCreatedNotificationScope?> CurrentScope = new();

    private readonly TenantCreatedNotificationScope? _previous;
    private readonly IOctoAdminSession _session;
    private readonly List<(string TenantId, Guid CorrelationId, ITenantNotifications Notifications)> _pending = [];
    private bool _disposed;

    private TenantCreatedNotificationScope(TenantCreatedNotificationScope? previous, IOctoAdminSession session)
    {
        _previous = previous;
        _session = session;
    }

    /// <summary>The scope of the current async flow, if an operation opened one.</summary>
    public static TenantCreatedNotificationScope? Current => CurrentScope.Value;

    /// <summary>Collects the tenant-created notifications of creates and attaches in <paramref name="session" />.</summary>
    public static TenantCreatedNotificationScope Begin(IOctoAdminSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var scope = new TenantCreatedNotificationScope(CurrentScope.Value, session);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    ///     Records a notification to be published by <see cref="PublishAsync" /> when it belongs to the scope's session.
    /// </summary>
    /// <returns><c>false</c> for another session: the caller publishes as without a scope.</returns>
    public bool TryAdd(IOctoAdminSession session, string tenantId, Guid correlationId,
        ITenantNotifications notifications)
    {
        if (_disposed || !ReferenceEquals(session, _session))
        {
            return false;
        }

        _pending.Add((tenantId, correlationId, notifications));
        return true;
    }

    /// <summary>
    ///     Publishes the collected notifications (once). Call it only after the transaction committed. A failure is
    ///     thrown to the caller.
    /// </summary>
    public async Task PublishAsync()
    {
        var pending = _pending.ToList();
        _pending.Clear();
        foreach (var (tenantId, correlationId, notifications) in pending)
        {
            // Bounded like the after-commit callback, so the owning operation does not wait on a hanging publish.
            await notifications.NotifyPosTenantCreateAsync(tenantId, correlationId)
                .WaitAsync(TenantContext.PosTenantCreateAttemptTimeout);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending.Clear();
        CurrentScope.Value = _previous;
    }
}
