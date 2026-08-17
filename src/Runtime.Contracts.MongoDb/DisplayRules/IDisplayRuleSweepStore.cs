namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;

/// <summary>
///     Durable store for display-rule backfill sweep tasks (AB#4812), modeled on
///     <see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle.ITenantSetupRetryStore" />:
///     persist a pending task, claim it lease-protected from a background service, mark it done or
///     record the failure for a bounded retry.
/// </summary>
public interface IDisplayRuleSweepStore
{
    /// <summary>
    ///     Enqueues (or refreshes) a sweep task for a (tenant, CK type). Idempotent — a pending
    ///     task for the same key is reset so a newer rule change restarts the retry budget.
    /// </summary>
    Task EnqueueAsync(string tenantId, string ckTypeId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Atomically claims the next due task (oldest attempt first): not exhausted, retry
    ///     interval elapsed, no active lease. Returns null when nothing is due.
    /// </summary>
    Task<DisplayRuleSweepRecord?> TryClaimAsync(string leaseOwner, TimeSpan leaseDuration,
        TimeSpan minRetryInterval, int maxAttempts, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Marks a sweep task as done (removes it).
    /// </summary>
    Task CompleteAsync(string tenantId, string ckTypeId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a failed attempt and releases the lease so the task is retried after the retry
    ///     interval. Exhausted tasks stay in the collection as a record for operators.
    /// </summary>
    Task RecordFailureAsync(string tenantId, string ckTypeId, string error,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Lists sweep tasks, optionally filtered by tenant (diagnostics).
    /// </summary>
    Task<IReadOnlyList<DisplayRuleSweepRecord>> ListAsync(string? tenantId = null,
        CancellationToken cancellationToken = default);
}
