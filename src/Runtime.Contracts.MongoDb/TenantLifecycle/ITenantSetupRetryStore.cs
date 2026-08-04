namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

/// <summary>
/// Durable, per-service retry queue for tenant default-configuration setup (AB#4690).
/// </summary>
/// <remarks>
/// <para>
/// Every backend service sets a tenant up when it observes <c>PosCreateTenant</c> / <c>PosUpdateTenant</c>
/// and at startup. When that setup throws — e.g. the tenant database is briefly unreachable right after a
/// delete + recreate under the same name — the failure used to be logged and forgotten: the only services
/// with a retry were those on the standardized creator, and even there it was an in-memory list that a pod
/// restart erased. A tenant could therefore stay half-provisioned indefinitely (identity seeded no roles,
/// so no administrator could be provisioned) until someone restarted the service or an unrelated tenant
/// event happened to arrive.
/// </para>
/// <para>
/// This store makes the failure durable and lets <c>FailedTenantRetryBackgroundService</c> — which every
/// service already runs — drive the setup to completion, with a Mongo lease keeping the retry single-flight
/// across service instances.
/// </para>
/// </remarks>
public interface ITenantSetupRetryStore
{
    /// <summary>
    /// Records (or updates) a failed setup for <paramref name="serviceId"/> / <paramref name="tenantId"/>,
    /// incrementing the attempt count and releasing any lease so the entry becomes claimable again.
    /// </summary>
    Task RecordFailureAsync(string serviceId, string tenantId, string error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the pending entry after a successful setup. No-op when there is none.
    /// </summary>
    Task ClearAsync(string serviceId, string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the longest-waiting pending tenant of <paramref name="serviceId"/> that is still
    /// within its retry budget and whose last attempt is older than <paramref name="minRetryInterval"/>,
    /// stamping it with <paramref name="leaseOwner"/> for <paramref name="leaseDuration"/>. Returns
    /// <c>null</c> when nothing is due. A single find-and-update, so two instances never claim the same
    /// entry.
    /// </summary>
    Task<TenantSetupRetryRecord?> TryClaimAsync(string serviceId, string leaseOwner, TimeSpan leaseDuration,
        TimeSpan minRetryInterval, int maxAttempts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lease held by <paramref name="leaseOwner"/> so the entry becomes claimable again after the
    /// retry interval. No-op if the lease is held by someone else or already cleared.
    /// </summary>
    Task ReleaseLeaseAsync(string serviceId, string tenantId, string leaseOwner,
        CancellationToken cancellationToken = default);

    /// <summary>Returns all pending entries, optionally restricted to one service. For diagnostics.</summary>
    Task<IReadOnlyList<TenantSetupRetryRecord>> ListAsync(string? serviceId = null,
        CancellationToken cancellationToken = default);
}
