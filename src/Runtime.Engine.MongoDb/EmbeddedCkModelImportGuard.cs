using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     What an embedded/startup CK model import does, given the version a tenant already has (CK v2 F1.0, AB#5900).
/// </summary>
internal enum EmbeddedImportDecision
{
    /// <summary>Nothing installed, or an older version: import (an upgrade runs migrations as before).</summary>
    Import,

    /// <summary>Exactly this version is installed: short-circuit (the caller may retry pending migrations).</summary>
    AlreadyInstalled,

    /// <summary>A newer version of the same major is installed: skip, log INFO.</summary>
    SkipNewerInstalled,

    /// <summary>A higher major is installed: skip, log WARN — the service is too old for the tenant.</summary>
    SkipNewerMajorInstalled
}

/// <summary>
///     The installed version of a CK model, looked up by NAME (any state except <c>Importing</c>).
/// </summary>
/// <param name="ModelId">The installed model id.</param>
/// <param name="ModelState">Its state (<c>Available</c> or <c>ResolveFailed</c>).</param>
internal sealed record InstalledCkModel(CkModelId ModelId, ModelState ModelState);

/// <summary>
///     Downgrade guard for every embedded/startup CK model import (CK v2 F1.0-S1, AB#5900). A service whose embedded
///     model is older than the version installed in a tenant must never replace the newer version: the import
///     deletes every CK model row of the same NAME before it inserts, so an exact-id existence check (the previous
///     behaviour) silently reverted an additive System bump within a second of the next tenant resolve.
///     <para>
///         Explicit imports (<c>ImportCk</c> via CLI/API, <c>ITenantContext.ImportCkModelAsync(CkCompiledModelRoot)</c>)
///         are not guarded: a downgrade stays possible there and is logged WARN (platform-owner decision Q5).
///     </para>
/// </summary>
internal static class EmbeddedCkModelImportGuard
{
    /// <summary>
    ///     The decision table of plan §3.2. Pure function so it can be unit-tested without a database.
    /// </summary>
    internal static EmbeddedImportDecision Decide(CkModelId embedded, CkModelId? installed)
    {
        if (installed == null)
        {
            return EmbeddedImportDecision.Import;
        }

        var comparison = installed.Version.CompareTo(embedded.Version);
        if (comparison == 0)
        {
            return EmbeddedImportDecision.AlreadyInstalled;
        }

        if (comparison < 0)
        {
            return EmbeddedImportDecision.Import;
        }

        return installed.Version.Major > embedded.Version.Major
            ? EmbeddedImportDecision.SkipNewerMajorInstalled
            : EmbeddedImportDecision.SkipNewerInstalled;
    }

    /// <summary>
    ///     Returns the installed version of <paramref name="modelName" />, by name, in any state except
    ///     <c>Importing</c> (a model another service is importing right now is handled by the import lock). A
    ///     <c>ResolveFailed</c> model counts as installed: re-importing it would replace the row and, if the
    ///     resolve fails again, lose it.
    /// </summary>
    internal static async Task<InstalledCkModel?> FindInstalledAsync(ICkMongoDbRepositoryDataSource dataSource,
        string modelName)
    {
        using var session = await dataSource.CreateSessionAsync();
        var models = await dataSource.CkModels.FindManyAsync(session,
            m => m.ModelId == modelName && m.ModelState != ModelState.Importing);
        var installed = models.OrderByDescending(m => m.Id.Version).FirstOrDefault();
        return installed == null ? null : new InstalledCkModel(installed.Id, installed.ModelState);
    }

    /// <summary>
    ///     Looks up the installed version, decides, and logs + counts a skip. Callers act on the decision only;
    ///     a skip sends no tenant-update notification, runs no migration and leaves the CK cache alone.
    /// </summary>
    internal static async Task<(EmbeddedImportDecision Decision, InstalledCkModel? Installed)> EvaluateAsync(
        ICkMongoDbRepositoryDataSource dataSource, CkModelId embedded, string tenantId, ILogger logger)
    {
        var installed = await FindInstalledAsync(dataSource, embedded.Name);
        var decision = Decide(embedded, installed?.ModelId);
        switch (decision)
        {
            case EmbeddedImportDecision.SkipNewerInstalled:
                logger.LogInformation(
                    "Embedded CK model import skipped for tenant '{TenantId}': downgrade prevented, the tenant has '{InstalledModelId}', " +
                    "the service embeds '{EmbeddedModelId}'",
                    tenantId, installed!.ModelId, embedded);
                CkModelImportDiagnostics.RecordEmbeddedImportSkipped(embedded.Name,
                    CkModelImportDiagnostics.ReasonNewerInstalled);
                break;
            case EmbeddedImportDecision.SkipNewerMajorInstalled:
                logger.LogWarning(
                    "Embedded CK model import skipped for tenant '{TenantId}': the service embeds '{EmbeddedModelId}' (major {EmbeddedMajor}), " +
                    "the tenant has '{InstalledModelId}' (major {InstalledMajor}); this service is too old for the tenant",
                    tenantId, embedded, embedded.Version.Major, installed!.ModelId, installed.ModelId.Version.Major);
                CkModelImportDiagnostics.RecordEmbeddedImportSkipped(embedded.Name,
                    CkModelImportDiagnostics.ReasonNewerMajorInstalled);
                break;
            case EmbeddedImportDecision.AlreadyInstalled:
                logger.LogDebug("CK model '{CkModelId}' already installed in tenant '{TenantId}' ({ModelState})",
                    embedded, tenantId, installed!.ModelState);
                break;
        }

        return (decision, installed);
    }
}
