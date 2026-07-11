namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

/// <summary>
/// Durable store for per-tenant provisioning lifecycle state, persisted as a non-CK collection in the
/// system database. Replaces the in-memory guards / retry sets that were lost on pod restart and never
/// shared across service instances (AB#4348). Phase 1 introduces the record and its transitions; the
/// Phase 2 reconciler builds the single-flight lease and resume logic on top of the same store.
/// </summary>
public interface ITenantLifecycleStore
{
    /// <summary>Returns the record for a tenant, or <c>null</c> when none exists (treat as legacy/Active).</summary>
    Task<TenantLifecycleRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Returns all lifecycle records. Used by the Phase 2 reconciler to find non-terminal tenants.</summary>
    Task<IReadOnlyList<TenantLifecycleRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a <see cref="TenantLifecycleState.Creating"/> record if none exists yet. Never downgrades an
    /// existing record (an already-Active tenant re-running setup on startup stays Active), it only refreshes
    /// the database name / correlation id and transition timestamp.
    /// </summary>
    Task EnsureCreatingAsync(string tenantId, string? databaseName, Guid correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the phase while the tenant is still <see cref="TenantLifecycleState.Creating"/> (e.g. identity
    /// data pending vs. seeded). Optionally records the last error. No-op if the tenant is already Active.
    /// </summary>
    Task SetPhaseAsync(string tenantId, TenantLifecyclePhase phase, string? lastError = null,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the tenant fully provisioned and operational, clearing any pending error.</summary>
    Task MarkActiveAsync(string tenantId, string? databaseName = null,
        CancellationToken cancellationToken = default);

    /// <summary>Marks provisioning as terminally failed (retry budget exhausted); the tenant awaits an operator.</summary>
    Task MarkFailedAsync(string tenantId, string error, CancellationToken cancellationToken = default);

    /// <summary>Marks the tenant as being deleted (tombstone) until the database drop is confirmed complete.</summary>
    Task MarkDeletingAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Removes the lifecycle record entirely (the tenant is fully gone).</summary>
    Task RemoveAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims one <see cref="TenantLifecycleState.Creating"/> tenant whose lease is free or
    /// expired, stamping it with <paramref name="leaseOwner"/> for <paramref name="leaseDuration"/> and
    /// bumping its attempt count. Returns the claimed record (with the incremented attempt count), or
    /// <c>null</c> when nothing needs reconciling. The claim is a single MongoDB find-and-update, so at
    /// most one service instance can own a given tenant at a time (Phase 2 single-flight — AB#4348).
    /// </summary>
    Task<TenantLifecycleRecord?> TryClaimForReconcileAsync(string leaseOwner, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a reconcile lease held by <paramref name="leaseOwner"/> so the tenant becomes immediately
    /// claimable again. No-op if the lease is held by someone else or already cleared.
    /// </summary>
    Task ReleaseLeaseAsync(string tenantId, string leaseOwner, CancellationToken cancellationToken = default);
}
