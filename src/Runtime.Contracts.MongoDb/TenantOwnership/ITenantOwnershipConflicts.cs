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
///     two instances.
/// </summary>
public interface ITenantOwnershipConflicts
{
    /// <summary>The current conflicts, ordered by tenant id.</summary>
    IReadOnlyList<TenantOwnershipConflict> Current { get; }
}
