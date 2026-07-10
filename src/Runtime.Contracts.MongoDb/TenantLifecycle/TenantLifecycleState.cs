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
    /// Deletion requested. The record is a tombstone until the physical database drop is confirmed
    /// complete, so a concurrent Create can serialize against it (Phase 3).
    /// </summary>
    Deleting = 2,

    /// <summary>Provisioning failed terminally (retry budget exhausted) and needs operator attention.</summary>
    Failed = 3
}
