namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;

/// <summary>
///     One durable backfill-sweep task per (tenant, CK type) whose display rules changed on a CK
///     model import (AB#4812). Persisted in a non-CK system collection so the sweep survives the
///     process that observed the change; drained lease-protected by the sweep background service.
/// </summary>
public sealed class DisplayRuleSweepRecord
{
    /// <summary>Normalized id of the tenant whose entities need the display-field recompute.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Full CK type id (e.g. "EnergyIQ/Space") whose subtree is swept polymorphically.</summary>
    public string CkTypeId { get; set; } = string.Empty;

    /// <summary>Number of sweep attempts that have failed so far; bounds the retry budget.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Message of the most recent failure, for operators.</summary>
    public string? LastError { get; set; }

    /// <summary>When the sweep task was first enqueued.</summary>
    public DateTime EnqueuedUtc { get; set; }

    /// <summary>When the most recent attempt was made; drives both the retry interval and the claim order.</summary>
    public DateTime LastAttemptUtc { get; set; }

    /// <summary>Owner id of the current single-flight lease, or <c>null</c> when free.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>Expiry of the current lease; a lease in the past is free to claim.</summary>
    public DateTime? LeaseUntil { get; set; }
}
