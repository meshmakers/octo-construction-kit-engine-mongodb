using System.Runtime.CompilerServices;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     What one transaction did to a tenant, for its after-commit tenant-created notification: a create or attach
///     registers a new version of the tenant, a detach or drop in the same transaction removes it again. The notification
///     is published only for the last create or attach of the transaction, and not when the tenant was removed after it.
///     Kept per session (weakly referenced), so it needs no read of the registry and goes away with the session.
/// </summary>
internal static class TenantTransactionNotificationState
{
    private static readonly ConditionalWeakTable<IOctoAdminSession, Dictionary<string, Entry>> States = new();

    /// <summary>Records a create or attach; returns its version for <see cref="IsCurrentCreate" />.</summary>
    public static int RecordCreate(IOctoAdminSession session, string tenantId)
    {
        var states = States.GetOrCreateValue(session);
        lock (states)
        {
            var key = tenantId.NormalizeString();
            var version = states.TryGetValue(key, out var entry) ? entry.Version + 1 : 1;
            states[key] = new Entry(version, Removed: false);
            return version;
        }
    }

    /// <summary>Records a detach or drop of the tenant in this session's transaction.</summary>
    public static void RecordRemoval(IOctoAdminSession session, string tenantId)
    {
        var states = States.GetOrCreateValue(session);
        lock (states)
        {
            var key = tenantId.NormalizeString();
            var version = states.TryGetValue(key, out var entry) ? entry.Version : 0;
            states[key] = new Entry(version, Removed: true);
        }
    }

    /// <summary>
    ///     Whether the create or attach of <paramref name="version" /> is still the tenant's last change in the
    ///     transaction (not removed, not superseded by a later create or attach).
    /// </summary>
    public static bool IsCurrentCreate(IOctoAdminSession session, string tenantId, int version)
    {
        if (!States.TryGetValue(session, out var states))
        {
            return true;
        }

        lock (states)
        {
            return !states.TryGetValue(tenantId.NormalizeString(), out var entry) ||
                   (!entry.Removed && entry.Version == version);
        }
    }

    private readonly record struct Entry(int Version, bool Removed);
}
