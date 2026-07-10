namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;

/// <summary>
/// Sub-step reached within <see cref="TenantLifecycleState.Creating"/>, so a resumable reconciler
/// (Phase 2) can continue a stalled setup from where it left off instead of restarting it (AB#4348).
/// Phase 1 sets only the coarse values; finer phases can be added without a data migration.
/// </summary>
public enum TenantLifecyclePhase
{
    None = 0,

    /// <summary>Setup was initiated; nothing durable has been confirmed yet.</summary>
    SetupStarted = 1,

    /// <summary>The tenant's CK model is imported but identity default configuration is not yet seeded.</summary>
    IdentityDataPending = 2,

    /// <summary>Identity default configuration (roles/groups/clients) has been seeded.</summary>
    IdentityDataSeeded = 3,

    /// <summary>The tenant has been started and is operational.</summary>
    Started = 4
}
