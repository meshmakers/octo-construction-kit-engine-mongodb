namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

/// <summary>
/// One durable record per tenant describing where it is in its provisioning / deprovisioning
/// lifecycle. Stored as a non-CK document in the system database, keyed by <see cref="TenantId"/>
/// (AB#4348).
/// </summary>
public sealed class TenantLifecycleRecord
{
    /// <summary>Normalized tenant id. Unique key of the record.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Database name of the tenant, needed to complete an async drop after a restart.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>Correlation id tying this record to the tenant lifecycle events that drive it.</summary>
    public Guid CorrelationId { get; set; }

    public TenantLifecycleState State { get; set; }

    public TenantLifecyclePhase Phase { get; set; }

    /// <summary>Number of setup/retry attempts so far, used by the Phase 2 reconciler to bound retries.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last error message observed while the tenant was not yet Active.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime LastTransitionUtc { get; set; }

    /// <summary>
    /// Owner id of the current single-flight lease. Unused in Phase 1; the Phase 2 reconciler claims a
    /// tenant by conditionally setting this together with <see cref="LeaseUntil"/>.
    /// </summary>
    public string? LeaseOwner { get; set; }

    /// <summary>Expiry of the current lease; a lease with <see cref="LeaseUntil"/> in the past is free to claim.</summary>
    public DateTime? LeaseUntil { get; set; }
}
