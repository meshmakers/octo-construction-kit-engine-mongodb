using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;

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

    /// <summary>
    /// Deleting tombstones only: the owner system database of the orphaned foreign ownership marker the delete's
    /// metadata guard granted the deliberate override for (AB#8807,
    /// <see cref="TenantDeletionHandle.OverriddenForeignMarker" />); null when no override was granted. Together with <see cref="OverriddenForeignMarkerStampedUtc" /> it identifies
    /// that one marker: the drop honours the override only while exactly that marker is still on the database, so a
    /// database another instance re-created under the same name in the meantime is never dropped. Written before the
    /// metadata deletion commits, so the settle sweep finishes an interrupted delete with the same decision. Reset by
    /// <see cref="ITenantLifecycleStore.MarkDeletingAsync" />.
    /// </summary>
    public string? OverriddenForeignMarkerOwner { get; set; }

    /// <summary>
    /// Deleting tombstones only: the stamp time of the overridden foreign marker
    /// (see <see cref="OverriddenForeignMarkerOwner" />).
    /// </summary>
    public DateTime? OverriddenForeignMarkerStampedUtc { get; set; }

    /// <summary>
    /// The overridden foreign marker as one value, or null when the tombstone records no complete override.
    /// </summary>
    public TenantOwnershipMarkerIdentity? GetOverriddenForeignMarker() =>
        OverriddenForeignMarkerOwner != null && OverriddenForeignMarkerStampedUtc.HasValue
            ? new TenantOwnershipMarkerIdentity(OverriddenForeignMarkerOwner,
                DateTime.SpecifyKind(OverriddenForeignMarkerStampedUtc.Value, DateTimeKind.Utc))
            : null;

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
