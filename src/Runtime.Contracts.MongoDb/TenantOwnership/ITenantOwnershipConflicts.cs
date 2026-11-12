namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;

/// <summary>
///     A tenant this instance resolves although its database carries the ownership marker of another
///     OctoMesh instance on the same MongoDB server (AB#8796): a double registration, typically left
///     over from before the ownership marker existed (AB#4945). Both instances may be working on the
///     same database.
/// </summary>
/// <param name="TenantId">Normalized tenant id in this instance.</param>
/// <param name="DatabaseName">Normalized tenant database name.</param>
/// <param name="OwnerSystemDatabaseName">The system database of the instance the marker names.</param>
/// <param name="DetectedAtUtc">When this process first saw the conflict.</param>
public sealed record TenantOwnershipConflict(
    string TenantId,
    string DatabaseName,
    string OwnerSystemDatabaseName,
    DateTime DetectedAtUtc);

/// <summary>
///     The ownership conflicts this process has seen on tenant resolve (AB#8796). The service health
///     check reports <c>Degraded</c> while the list is not empty. Resolve itself is never refused and
///     no marker is overwritten; the operator resolves a conflict by detaching the tenant in one of the
///     two instances - here it is forgotten at once, in the other instance at the next
///     <see cref="RecheckAsync" />.
/// </summary>
public interface ITenantOwnershipConflicts
{
    /// <summary>The current conflicts, ordered by tenant id.</summary>
    IReadOnlyList<TenantOwnershipConflict> Current { get; }

    /// <summary>
    ///     Rechecks the recorded conflicts against their databases (AB#8796), at most once per bounded interval
    ///     (one minute in the engine): a conflict whose database no longer carries another instance's marker is
    ///     forgotten, so the health check returns to <c>Healthy</c> once the conflict is resolved; one that persists
    ///     stays. The health check calls it before it reads <see cref="Current" />; resolving a conflicting tenant
    ///     triggers it too. Never throws for a database it cannot read.
    /// </summary>
    Task RecheckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    ///     Why this instance could not stamp the system-database marker into its own system database (AB#8672), or
    ///     null while the marker is stamped (or the stamp was never attempted in this process). Without the marker the
    ///     other instances on the server recognise this system database only while it registers a tenant, so the
    ///     health check reports <c>Degraded</c> with this reason; the next successful stamp (every start of a
    ///     service that ensures the System CK model) clears it.
    /// </summary>
    string? SystemDatabaseMarkerProblem { get; }
}
