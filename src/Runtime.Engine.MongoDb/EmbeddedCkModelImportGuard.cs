using System.Collections.Concurrent;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
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
    ///     Keys (tenant, embedded id, installed id) whose skip has already been logged at INFO/WARN and counted in this
    ///     process (G-L2). <c>UpdateSystemCkModelAsync</c> runs on every tenant resolve — per GraphQL request in the
    ///     asset repository — so an older service would otherwise log and count the same prevented downgrade per
    ///     request. Bounded by tenants × models × installed versions.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedSkips = new(StringComparer.Ordinal);

    /// <summary>
    ///     Looks up the installed version, decides, and logs + counts a skip. Callers act on the decision only;
    ///     a skip sends no tenant-update notification, runs no migration and leaves the CK cache alone. This early
    ///     decision saves the lock wait; <c>ExecuteImport</c> repeats it under the import lock (concurrent imports).
    /// </summary>
    internal static async Task<(EmbeddedImportDecision Decision, InstalledCkModel? Installed)> EvaluateAsync(
        ICkMongoDbRepositoryDataSource dataSource, CkModelId embedded, string tenantId, ILogger logger)
    {
        var installed = await FindInstalledAsync(dataSource, embedded.Name);
        var decision = Decide(embedded, installed?.ModelId);
        Report(decision, embedded, installed, tenantId, logger, underLock: false);
        return (decision, installed);
    }

    /// <summary>
    ///     Logs a skip once per process at INFO (same major) or WARN (higher major) and counts it once; repeats
    ///     log at DEBUG only.
    /// </summary>
    internal static void Report(EmbeddedImportDecision decision, CkModelId embedded, InstalledCkModel? installed,
        string? tenantId, ILogger logger, bool underLock)
    {
        if (decision == EmbeddedImportDecision.AlreadyInstalled)
        {
            logger.LogDebug("CK model '{CkModelId}' already installed in tenant '{TenantId}' ({ModelState})",
                embedded, tenantId, installed!.ModelState);
            return;
        }

        if (decision == EmbeddedImportDecision.Import)
        {
            return;
        }

        var first = ReportedSkips.TryAdd($"{tenantId}|{embedded.FullName}|{installed!.ModelId.FullName}", 0);
        var where = underLock ? " (installed concurrently by another service, detected under the import lock)" : "";
        if (decision == EmbeddedImportDecision.SkipNewerInstalled)
        {
            logger.Log(first ? LogLevel.Information : LogLevel.Debug,
                "Embedded CK model import skipped for tenant '{TenantId}': downgrade prevented, the tenant has '{InstalledModelId}', " +
                "the service embeds '{EmbeddedModelId}'{Where}",
                tenantId, installed.ModelId, embedded, where);
        }
        else
        {
            logger.Log(first ? LogLevel.Warning : LogLevel.Debug,
                "Embedded CK model import skipped for tenant '{TenantId}': the service embeds '{EmbeddedModelId}' (major {EmbeddedMajor}), " +
                "the tenant has '{InstalledModelId}' (major {InstalledMajor}); this service is too old for the tenant{Where}",
                tenantId, embedded, embedded.Version.Major, installed.ModelId, installed.ModelId.Version.Major, where);
        }

        if (first)
        {
            CkModelImportDiagnostics.RecordEmbeddedImportSkipped(embedded.Name,
                decision == EmbeddedImportDecision.SkipNewerInstalled
                    ? CkModelImportDiagnostics.ReasonNewerInstalled
                    : CkModelImportDiagnostics.ReasonNewerMajorInstalled);
        }
    }

    /// <summary>
    ///     The <see cref="OperationResult" /> warning of a skipped embedded import, so callers (blueprint install,
    ///     service setup) can report the real reason instead of a generic "not installed".
    /// </summary>
    internal static OperationMessage SkipWarning(CkModelId embedded, InstalledCkModel installed) =>
        new(MessageLevel.Warning, null, 0,
            $"Import of CK model '{embedded}' skipped: the tenant has '{installed.ModelId}' ({installed.ModelState}); " +
            "an embedded/startup import never replaces a newer version (use an explicit ImportCk to downgrade).");
}
