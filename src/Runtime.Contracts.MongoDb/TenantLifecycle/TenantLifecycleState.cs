namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

/// <summary>
/// Durable lifecycle state of a tenant's provisioning / deprovisioning, persisted in the system
/// database so it survives service restarts and is shared across service instances. Replaces the
/// in-memory guards and retry sets that were lost on pod restart (AB#4348).
/// </summary>
public enum TenantLifecycleState
{
    /// <summary>
    /// Setup has started but the tenant is not yet fully provisioned — e.g. the CK model is imported
    /// but the identity default configuration / roles are not yet seeded.
    /// </summary>
    Creating = 0,

    /// <summary>Fully provisioned and operational: identity data seeded and the tenant started.</summary>
    Active = 1,

    /// <summary>
    /// Deletion requested or settling. The record is a tombstone from the moment the delete starts
    /// until the settle sweep has confirmed nothing in flight resurrected the tenant's retry rows or
    /// database (~90–120 s after the drop, AB#4829); a concurrent Create serializes against it with a
    /// retryable 409 (Phase 3). No writer other than the delete/sweep may leave this state — every
    /// other transition preserves a Deleting record untouched.
    /// </summary>
    Deleting = 2,

    /// <summary>Provisioning failed terminally (retry budget exhausted) and needs operator attention.</summary>
    Failed = 3
}
