using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
/// Identifies the database source for a CK-model repository operation. <see cref="TenantId"/>
/// is required by audit-trail consumers (e.g. <c>ICkModelImportAuditTrail</c>) to route
/// notifications to the correct tenant event log; <c>null</c> denotes the system tenant.
/// <para>
/// <see cref="IncludeResolveFailedModels"/> (CK v2 F1.0-S2, AB#5901) lets the dependency resolver
/// see <c>ResolveFailed</c> models next to <c>Available</c> ones. Only the post-import re-validation
/// (<c>DatabaseCkModelRepository.ValidateDependencies</c>) sets it — the CK cache, imports and every
/// other lookup keep resolving <c>Available</c> models only.
/// </para>
/// </summary>
/// <para>
/// <see cref="GuardAgainstDowngrade"/> (CK v2 F1.0-S1, AB#5900) marks an embedded/startup import: under the model
/// import lock, <c>ExecuteImport</c> repeats the by-name decision of <see cref="EmbeddedCkModelImportGuard"/> and
/// skips when the tenant already has this or a newer version — a concurrent import by another service may have
/// installed it while this caller waited for the lock. The skip is reported in <see cref="ImportOutcome"/>.
/// </para>
/// </summary>
public record TenantDatabaseSourceIdentifier(
    IOctoSession? Session,
    ICkMongoDbRepositoryDataSource MongoDbRepositoryDataSource,
    string? TenantId = null,
    bool IncludeResolveFailedModels = false,
    bool GuardAgainstDowngrade = false)
{
    /// <summary>
    ///     Filled by <c>DatabaseCkModelRepository.UpdateModelAsync</c>: what the import did (CK v2 F1.0, AB#5900).
    /// </summary>
    public CkModelImportOutcome ImportOutcome { get; } = new();
}

/// <summary>
///     Result of a CK model import as seen by the repository (CK v2 F1.0, AB#5900 / AB#5901).
/// </summary>
public sealed class CkModelImportOutcome
{
    /// <summary>
    ///     True when an embedded import (<see cref="TenantDatabaseSourceIdentifier.GuardAgainstDowngrade" />) was
    ///     skipped under the import lock because the tenant already had this or a newer version.
    /// </summary>
    public bool SkippedUnderLock { get; internal set; }

    /// <summary>The installed model that made the import skip.</summary>
    public CkModelId? InstalledModelId { get; internal set; }

    /// <summary>Models the post-import re-validation returned from <c>ResolveFailed</c> to <c>Available</c>.</summary>
    public IReadOnlyCollection<CkModelId> RecoveredModelIds { get; internal set; } = [];
}
