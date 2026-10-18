using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UpgradeGuard;

/// <summary>
///     Tenant upgrade guard (CK v2 F2.5, AB#6867): computes, before anything is imported, which installed models an
///     update set of CK models would break. Read-only. Callers normally use
///     <c>ITenantContext.EvaluateCkUpdateAsync</c>, which supplies the tenant's data source.
/// </summary>
public interface ITenantUpgradeGuard
{
    /// <summary>
    ///     Judges <paramref name="updateSet" /> against the models persisted in the tenant of
    ///     <paramref name="source" />. See <c>ITenantContext.EvaluateCkUpdateAsync</c> for the rules.
    /// </summary>
    /// <param name="source">The tenant's database source.</param>
    /// <param name="updateSet">The compiled candidates to be imported together.</param>
    /// <param name="availableDependentVersions">Optional versions offered to fill the suggestion of a blocker.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <param name="includeServiceManagedModels">
    ///     True for explicit callers (file import, its preview): service-managed candidates (<c>System</c>, <c>System.*</c>)
    ///     are judged as replacements too. False (default) keeps them out, as for every service-driven path (AB#6966).
    /// </param>
    Task<CkUpgradeGuardResult> EvaluateAsync(TenantDatabaseSourceIdentifier source,
        IReadOnlyCollection<CkCompiledModelRoot> updateSet,
        IReadOnlyCollection<CkModelId>? availableDependentVersions = null,
        CancellationToken cancellationToken = default, bool includeServiceManagedModels = false);
}
