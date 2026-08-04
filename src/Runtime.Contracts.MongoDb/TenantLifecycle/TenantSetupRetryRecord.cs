namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

/// <summary>
/// One durable record per (service, tenant) whose default-configuration setup failed, so the failure
/// survives the process that observed it (AB#4690).
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="TenantLifecycleRecord"/>: the lifecycle record describes the
/// tenant itself and has a single writer (the asset repository), whereas every service runs its own
/// setup and needs its own retry bookkeeping. Keying by <see cref="ServiceId"/> keeps those independent.
/// </remarks>
public sealed class TenantSetupRetryRecord
{
    /// <summary>Identifies the service that failed to set the tenant up (its assembly name by default).</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Normalized id of the tenant whose setup failed.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Number of setup attempts that have failed so far; bounds the retry budget.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Message of the most recent failure, for operators.</summary>
    public string? LastError { get; set; }

    /// <summary>When the first failure of the current streak was recorded.</summary>
    public DateTime FirstFailureUtc { get; set; }

    /// <summary>When the most recent attempt was made; drives both the retry interval and the claim order.</summary>
    public DateTime LastAttemptUtc { get; set; }

    /// <summary>Owner id of the current single-flight retry lease, or <c>null</c> when free.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>Expiry of the current lease; a lease in the past is free to claim.</summary>
    public DateTime? LeaseUntil { get; set; }
}
