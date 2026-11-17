using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>The post notifications of a tenant that are published only after the transaction that caused them committed.</summary>
internal enum TenantPostNotificationKind
{
    /// <summary><c>PosCreateTenant</c> of a create or attach.</summary>
    Created,

    /// <summary><c>PosDeleteTenant</c> of a drop or detach.</summary>
    Deleted
}

/// <summary>
///     Lets an engine operation that owns its transaction publish the tenant-created and tenant-deleted notifications
///     itself, after its commit, so a failed publish reaches that operation instead of being logged only.
/// </summary>
/// <remarks>
///     A create or attach registers the tenant, a drop or detach unregisters it, in the caller's transaction. Other
///     services set a tenant up or tear it down when they receive the notification, so it must not be published before
///     the registry change is committed. By default a create, attach, drop or detach inside a transaction publishes after
///     the commit (session callback, a few attempts, then a warning). An engine operation that starts and commits the
///     transaction itself (backup with detach, restore with drop and attach) runs the change inside <see cref="Begin" />
///     with its session: the notification of a change in exactly that session is collected here, and the operation calls
///     <see cref="PublishAsync" /> after its commit, where a failure is reported to its own caller. Any other session in
///     the same async flow is not affected. The scope flows with the async call (<see cref="AsyncLocal{T}" />); nothing
///     is published when the operation never calls <see cref="PublishAsync" /> (a failed change, an aborted
///     transaction).
/// </remarks>
internal sealed class TenantNotificationScope : IDisposable
{
    private static readonly AsyncLocal<TenantNotificationScope?> CurrentScope = new();

    private readonly TenantNotificationScope? _previous;
    private readonly IOctoAdminSession _session;

    private readonly List<(TenantPostNotificationKind Kind, string TenantId, Guid CorrelationId,
        ITenantNotifications Notifications)> _pending = [];

    private bool _disposed;

    private TenantNotificationScope(TenantNotificationScope? previous, IOctoAdminSession session)
    {
        _previous = previous;
        _session = session;
    }

    /// <summary>The scope of the current async flow, if an operation opened one.</summary>
    public static TenantNotificationScope? Current => CurrentScope.Value;

    /// <summary>Collects the post notifications of creates, attaches, drops and detaches in <paramref name="session" />.</summary>
    public static TenantNotificationScope Begin(IOctoAdminSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var scope = new TenantNotificationScope(CurrentScope.Value, session);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    ///     Records a notification to be published by <see cref="PublishAsync" /> when it belongs to the scope's session.
    /// </summary>
    /// <returns><c>false</c> for another session: the caller publishes as without a scope.</returns>
    public bool TryAdd(IOctoAdminSession session, TenantPostNotificationKind kind, string tenantId,
        Guid correlationId, ITenantNotifications notifications)
    {
        if (_disposed || !ReferenceEquals(session, _session))
        {
            return false;
        }

        _pending.Add((kind, tenantId, correlationId, notifications));
        return true;
    }

    /// <summary>
    ///     Publishes the collected notifications (once), in the order of the changes. Call it only after the transaction
    ///     committed. The first failure is thrown to the caller; the notifications after it are not published.
    /// </summary>
    public async Task PublishAsync()
    {
        var pending = _pending.ToList();
        _pending.Clear();
        foreach (var (kind, tenantId, correlationId, notifications) in pending)
        {
            var publish = kind == TenantPostNotificationKind.Created
                ? notifications.NotifyPosTenantCreateAsync(tenantId, correlationId)
                : notifications.NotifyPosTenantDeleteAsync(tenantId, correlationId);

            // Bounded like the after-commit callback, so the owning operation does not wait on a hanging publish.
            await publish.WaitAsync(TenantContext.PosTenantCreateAttemptTimeout);
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
