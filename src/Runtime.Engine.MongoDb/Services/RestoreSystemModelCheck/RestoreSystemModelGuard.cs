using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>The outcome of the restore check of service-managed models.</summary>
/// <param name="Decisions">One decision per service-managed model of the dump (after a raise: of the re-check).</param>
/// <param name="Refusals">The refused models, each with its reason; empty when the restore may attach.</param>
internal sealed record RestoreSystemModelCheckResult(
    IReadOnlyList<RestoreSystemModelDecision> Decisions,
    IReadOnlyList<RestoreSystemModelDecision> Refusals)
{
    /// <summary>True when the restore must not attach the database.</summary>
    public bool IsRefused => Refusals.Count > 0;

    /// <summary>The models that were raised to the platform's version.</summary>
    public IReadOnlyList<RestoreSystemModelDecision> Raised { get; init; } = [];

    /// <summary>
    ///     True when the check created the tenant database user for the raise and kept it (keepDatabaseUser): the caller
    ///     drops it again when the attach after the check fails.
    /// </summary>
    public bool KeptCreatedDatabaseUser { get; init; }

    /// <summary>
    ///     The refusal message: starts with <see cref="RestoreSystemModelGuard.RefusalCode" />, names every refused model
    ///     with its version and reason, never model content.
    /// </summary>
    public string Message(string tenantId) =>
        $"{RestoreSystemModelGuard.RefusalCode}: the restore of tenant '{tenantId}' was refused before the attach. " +
        string.Join(" ", Refusals.Select(r =>
            (r.ModelId == null ? "" : $"'{r.ModelId}' ") +
            $"({RestoreSystemModelGuard.ReasonTag(r.Reason!.Value)}): {r.Detail}.")) +
        " The tenant was not attached. A tenant restore accepts service-managed models (System, System.*) only as " +
        "published in the CK catalog.";
}

/// <summary>
///     The restore check of service-managed CK models (System, System.*): runs between the mongorestore of a dump and the
///     attach, so a tenant never becomes reachable with a service-managed model that differs from a published one.
///     <list type="bullet">
///         <item>Every model row of a service-managed model in the restored database (any state) and its element rows are
///         read back the way the runtime CK cache reads them and compared, in canonical form, with the catalog's model of
///         the same id (<see cref="CkCompiledModelCanonicalizer" />). Element rows that touch a service-managed model but
///         do not belong to its model row are refused too.</item>
///         <item><see cref="RestoreSystemModelDecider" /> decides keep, raise or refuse per model; the platform's version is
///         the one installed in the system tenant.</item>
///         <item>A genuine older model is raised to the platform's version through the guarded system import
///         (<c>ITenantContext.ImportCkModelAsync(CkModelId, OperationResult)</c>, catalog content and migrations) while the
///         database is still unattached; <see cref="RestoringTenantScope" /> lets the migrations resolve the tenant. The
///         result is checked again.</item>
///         <item>The catalog, the system tenant or a model that cannot be read fail the check closed.</item>
///     </list>
/// </summary>
internal sealed class RestoreSystemModelGuard(
    ILogger<RestoreSystemModelGuard> logger,
    ICatalogService catalogService)
{
    /// <summary>Stable error code of the refusal, at the start of the restore's failure message.</summary>
    public const string RefusalCode = "RESTORE_SYSTEM_MODEL_REFUSED";

    /// <summary>How often the raise loop retries models whose dependencies were raised in an earlier pass.</summary>
    private const int MaxRaisePasses = 4;

    /// <summary>The <c>reason</c> tag and message token of <paramref name="reason" />.</summary>
    internal static string ReasonTag(RestoreSystemModelRefusalReason reason) => reason switch
    {
        RestoreSystemModelRefusalReason.ContentDiffers => "content_differs",
        RestoreSystemModelRefusalReason.UnknownVersion => "unknown_version",
        RestoreSystemModelRefusalReason.HigherMajor => "higher_major",
        RestoreSystemModelRefusalReason.UnknownModel => "unknown_model",
        RestoreSystemModelRefusalReason.BelowFloor => "below_floor",
        RestoreSystemModelRefusalReason.TargetNotPublished => "target_not_published",
        RestoreSystemModelRefusalReason.InconsistentRows => "inconsistent_rows",
        RestoreSystemModelRefusalReason.MultipleVersions => "multiple_versions",
        RestoreSystemModelRefusalReason.CatalogUnavailable => "catalog_unavailable",
        RestoreSystemModelRefusalReason.SystemTenantUnavailable => "system_tenant_unavailable",
        RestoreSystemModelRefusalReason.ModelUnreadable => "model_unreadable",
        RestoreSystemModelRefusalReason.UpgradeFailed => "upgrade_failed",
        RestoreSystemModelRefusalReason.DependencyUnpublished => "dependency_unpublished",
        _ => "unknown"
    };

    /// <summary>
    ///     Checks the restored, unattached <paramref name="databaseName" /> and raises genuine older models. A refusal is
    ///     logged as WARN and counted; the caller must then not attach the database.
    /// </summary>
    /// <param name="systemContext">The system context of this instance (registry, system tenant, admin client).</param>
    /// <param name="tenantId">The tenant being restored.</param>
    /// <param name="databaseName">The restored database.</param>
    /// <param name="keepDatabaseUser">
    ///     True when the restore attaches afterwards: a tenant database user this check created for the migrations of a
    ///     raise stays (the attach needs it). False: a user created here is dropped again.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    internal async Task<RestoreSystemModelCheckResult> EnforceAsync(TenantContext systemContext, string tenantId,
        string databaseName, bool keepDatabaseUser, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(systemContext, tenantId, databaseName, cancellationToken);
        if (check.IsRefused)
        {
            return Refused(tenantId, check);
        }

        var raises = check.Decisions.Where(d => d.Action == RestoreSystemModelAction.Raise).ToList();
        if (raises.Count == 0)
        {
            return check;
        }

        var userCreated = false;
        var succeeded = false;
        try
        {
            userCreated = await systemContext.EnsureTenantDatabaseUserForRestoreAsync(databaseName);
            var failure = await RaiseAsync(systemContext, tenantId, databaseName, raises, cancellationToken);
            if (failure != null)
            {
                return Refused(tenantId, new RestoreSystemModelCheckResult(check.Decisions, [failure]));
            }

            // The raise went through the import path; the result must pass the same check as a dump: every model is
            // genuine now and none would be raised again.
            var recheck = await CheckAsync(systemContext, tenantId, databaseName, cancellationToken);
            var notReached = recheck.Refusals
                .Concat(recheck.Decisions.Where(d => d.Action == RestoreSystemModelAction.Raise))
                .Select(d => RestoreSystemModelDecision.Refuse(d.ModelId, RestoreSystemModelRefusalReason.UpgradeFailed,
                    $"after raising the dump's older models it is still not the platform's genuine version ({d.Detail})"))
                .ToList();
            if (notReached.Count > 0)
            {
                return Refused(tenantId, new RestoreSystemModelCheckResult(recheck.Decisions, notReached));
            }

            foreach (var raise in raises)
            {
                logger.LogInformation(
                    "Restore of tenant '{TenantId}': genuine service-managed CK model '{DumpModelId}' raised to the " +
                    "platform's '{TargetModelId}' before the attach", tenantId, raise.ModelId, raise.Target);
                CkModelImportDiagnostics.RecordRestoreSystemModelRaised(raise.Target!.Name);
            }

            succeeded = true;
            return recheck with { Raised = raises, KeptCreatedDatabaseUser = userCreated && keepDatabaseUser };
        }
        finally
        {
            if (!succeeded)
            {
                // A raise that did not complete may have loaded the restoring tenant id into this process's CK cache.
                systemContext.UnloadCkCacheForRestore(tenantId);
            }

            if (userCreated && (!succeeded || !keepDatabaseUser))
            {
                await systemContext.DropTenantDatabaseUserForRestoreAsync(databaseName);
            }
        }
    }

    /// <summary>Gathers the facts of every service-managed model of the restored database and decides.</summary>
    internal async Task<RestoreSystemModelCheckResult> CheckAsync(TenantContext systemContext, string tenantId,
        string databaseName, CancellationToken cancellationToken)
    {
        IReadOnlyList<(CkModel Row, CkCompiledModelRoot Compiled)> dumpModels;
        IReadOnlyList<string> foreignRows;
        try
        {
            var dataSource = systemContext.CreateAdminDataSourceForRestore(databaseName, tenantId.NormalizeString());
            dumpModels = await DatabaseCkModelRepository.ReadServiceManagedModelsAsync(dataSource);
            foreignRows = await DatabaseCkModelRepository.FindForeignServiceManagedElementRowsAsync(dataSource,
                dumpModels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Restore of tenant '{TenantId}': the service-managed CK models of database " +
                                "'{DatabaseName}' could not be read", tenantId, databaseName);
            return Only(null, RestoreSystemModelRefusalReason.ModelUnreadable,
                "the service-managed CK models of the restored database could not be read");
        }

        var refusals = new List<RestoreSystemModelDecision>();
        foreach (var finding in foreignRows.Take(5))
        {
            refusals.Add(RestoreSystemModelDecision.Refuse(null,
                RestoreSystemModelRefusalReason.InconsistentRows, finding));
        }

        foreach (var group in dumpModels.GroupBy(m => m.Row.ModelId, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            refusals.Add(RestoreSystemModelDecision.Refuse(group.First().Row.Id,
                RestoreSystemModelRefusalReason.MultipleVersions,
                $"the dump holds {group.Count()} versions of '{group.Key}' " +
                $"({string.Join(", ", group.Select(m => m.Row.Id.Version))})"));
        }

        if (refusals.Count > 0)
        {
            return new RestoreSystemModelCheckResult([], refusals);
        }

        IReadOnlyList<(CkModel Row, CkCompiledModelRoot Compiled)> platformModels;
        try
        {
            var systemDataSource = systemContext.CreateAdminDataSourceForRestore(systemContext.DatabaseName,
                systemContext.TenantId);
            platformModels = await DatabaseCkModelRepository.ReadServiceManagedModelsAsync(systemDataSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Restore of tenant '{TenantId}': the service-managed CK models of the system tenant " +
                                "could not be read", tenantId);
            return Only(null, RestoreSystemModelRefusalReason.SystemTenantUnavailable,
                "the platform's versions (system tenant) could not be read");
        }

        var decisions = new List<RestoreSystemModelDecision>();
        foreach (var (row, compiled) in dumpModels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreSystemModelDecision decision;
            try
            {
                var facts = await GatherFactsAsync(row.Id, compiled, platformModels);
                decision = RestoreSystemModelDecider.Decide(facts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Restore of tenant '{TenantId}': the CK catalog could not be read for " +
                                    "'{CkModelId}'", tenantId, row.Id);
                decision = RestoreSystemModelDecision.Refuse(row.Id, RestoreSystemModelRefusalReason.CatalogUnavailable,
                    "the CK catalog could not be read, so the model cannot be verified");
            }

            decisions.Add(decision);
        }

        return new RestoreSystemModelCheckResult(decisions,
            decisions.Where(d => d.Action == RestoreSystemModelAction.Refuse).ToList());
    }

    private async Task<RestoreSystemModelFacts> GatherFactsAsync(CkModelId dumpModelId, CkCompiledModelRoot restored,
        IReadOnlyList<(CkModel Row, CkCompiledModelRoot Compiled)> platformModels)
    {
        var name = dumpModelId.Name;
        var floor = ServiceManagedCkModelFloors.FloorOf(name);
        var versions = await catalogService.ListVersionsAsync(name);
        int? highestMajor = versions.Count == 0 ? null : versions.Max(v => v.ModelId.Version.Major);

        var published = await catalogService.IsExistingAsync(dumpModelId);
        var contentEquals = false;
        IReadOnlyList<CkModelId> unpublishedClosure = [];
        if (published)
        {
            var catalogModel = await GetPublishedAsync(dumpModelId);
            var comparison = CompareContent(restored, catalogModel);
            contentEquals = comparison.IsGenuineContent;
            if (contentEquals)
            {
                unpublishedClosure = await UnpublishedClosureEntriesAsync(comparison);
            }
        }

        var platform = platformModels
            .Where(m => string.Equals(m.Row.Id.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(m.Row.ModelId, m.Row.Id.Name, StringComparison.Ordinal) &&
                        m.Row.ModelState != ModelState.Importing)
            .OrderByDescending(m => m.Row.Id.Version)
            .Select(m => ((CkModel Row, CkCompiledModelRoot Compiled)?)m)
            .FirstOrDefault();

        var platformIdentical = false;
        if (platform is { } p && p.Row.Id.Version.Major == dumpModelId.Version.Major &&
            p.Row.Id.Version.CompareTo(dumpModelId.Version) > 0 && await catalogService.IsExistingAsync(p.Row.Id))
        {
            var platformComparison = CompareContent(p.Compiled, await GetPublishedAsync(p.Row.Id));
            platformIdentical = platformComparison.IsGenuineContent &&
                                (await UnpublishedClosureEntriesAsync(platformComparison)).Count == 0;
        }

        return new RestoreSystemModelFacts(dumpModelId, published, contentEquals, highestMajor, platform?.Row.Id,
            platformIdentical, floor, unpublishedClosure);
    }

    private async Task<CkCompiledModelRoot> GetPublishedAsync(CkModelId modelId)
    {
        var operationResult = new OperationResult();
        var model = await catalogService.GetAsync(modelId, operationResult);
        if (model == null || operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException($"The CK catalog returned no readable model for '{modelId}'.");
        }

        return model;
    }

    /// <summary>
    ///     True when the model read back from a tenant database has the canonical content of the catalog's model. Values
    ///     a tenant added to an extensible enum of the model (enum customization) are not part of the published content and
    ///     are left out; values of a non-extensible enum and every other member count.
    /// </summary>
    internal static bool IsSameContent(CkCompiledModelRoot readBack, CkCompiledModelRoot published) =>
        CompareContent(readBack, published).IsGenuineContent;

    /// <summary>
    ///     Compares the model read back from a tenant database with the catalog's model like <see cref="IsSameContent" />
    ///     and names the entries of a resolved dependency closure that moved within the declared ranges
    ///     (<see cref="RestoreSystemModelClosure" />); the caller checks that they are published.
    /// </summary>
    internal static RestoreSystemModelContentComparison CompareContent(CkCompiledModelRoot readBack,
        CkCompiledModelRoot published)
    {
        var extensibleEnums = (published.Enums ?? [])
            .Where(e => e.IsExtensible)
            .Select(e => e.EnumId.ToString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var ckEnum in readBack.Enums ?? [])
        {
            if (extensibleEnums.Contains(ckEnum.EnumId.ToString()))
            {
                ckEnum.Values = ckEnum.Values.Where(v => !v.IsExtension).ToList();
            }
        }

        return RestoreSystemModelClosure.Compare(readBack, published);
    }

    /// <summary>
    ///     The entries of <paramref name="comparison" />'s moved closure that the catalog does not publish: a build of the
    ///     published model can only have resolved published versions, so such an entry is not a build artefact.
    /// </summary>
    private async Task<IReadOnlyList<CkModelId>> UnpublishedClosureEntriesAsync(
        RestoreSystemModelContentComparison comparison)
    {
        var unpublished = new List<CkModelId>();
        foreach (var entry in comparison.MovedClosureEntries)
        {
            if (!await catalogService.IsExistingAsync(entry))
            {
                unpublished.Add(entry);
            }
        }

        return unpublished;
    }

    /// <summary>
    ///     Raises each genuine older model to its target in the unattached database, in dependency order (models whose
    ///     dependencies are raised in the same restore are retried in the next pass). Returns the refusal of the first
    ///     model that does not reach its target, or null.
    /// </summary>
    private async Task<RestoreSystemModelDecision?> RaiseAsync(TenantContext systemContext, string tenantId,
        string databaseName, IReadOnlyList<RestoreSystemModelDecision> raises, CancellationToken cancellationToken)
    {
        using var scope = RestoringTenantScope.Begin(tenantId, databaseName);
        var restoreContext = systemContext.CreateRestoreTenantContext(tenantId, databaseName);
        var dataSource = systemContext.CreateAdminDataSourceForRestore(databaseName, tenantId.NormalizeString());

        // System first: every other service-managed model depends on it.
        var pending = raises
            .OrderBy(r => string.Equals(r.Target!.Name, "System", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Target!.Name, StringComparer.Ordinal)
            .ToList();
        var lastProblem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < MaxRaisePasses && pending.Count > 0; pass++)
        {
            var stillPending = new List<RestoreSystemModelDecision>();
            foreach (var raise in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operationResult = new OperationResult();
                try
                {
                    await restoreContext.ImportCkModelAsync(raise.Target!, operationResult);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Restore of tenant '{TenantId}': raising '{DumpModelId}' to '{TargetModelId}' " +
                                        "failed", tenantId, raise.ModelId, raise.Target);
                    return RestoreSystemModelDecision.Refuse(raise.ModelId,
                        RestoreSystemModelRefusalReason.UpgradeFailed,
                        $"raising it to the platform's '{raise.Target}' failed ({ex.GetType().Name})");
                }

                var name = raise.Target!.Name;
                var installed = await EmbeddedCkModelImportGuard.FindInstalledAsync(dataSource, name);
                if (installed?.ModelId == raise.Target && installed.ModelState == ModelState.Available)
                {
                    continue;
                }

                lastProblem[name] = operationResult.HasErrors || operationResult.HasFatalErrors
                    ? "the import reported errors"
                    : $"the tenant holds '{installed?.ModelId.ToString() ?? "none"}' " +
                      $"({installed?.ModelState.ToString() ?? "-"}) after the import";
                stillPending.Add(raise);
            }

            if (stillPending.Count == pending.Count)
            {
                break;
            }

            pending = stillPending;
        }

        if (pending.Count == 0)
        {
            return null;
        }

        var first = pending[0];
        return RestoreSystemModelDecision.Refuse(first.ModelId, RestoreSystemModelRefusalReason.UpgradeFailed,
            $"raising it to the platform's '{first.Target}' did not complete: {lastProblem.GetValueOrDefault(first.Target!.Name)}");
    }

    /// <summary>Logs every refusal of <paramref name="result" /> as WARN and counts it; returns the result.</summary>
    internal RestoreSystemModelCheckResult ReportRefusal(string tenantId, RestoreSystemModelCheckResult result) =>
        Refused(tenantId, result);

    private RestoreSystemModelCheckResult Refused(string tenantId, RestoreSystemModelCheckResult result)
    {
        foreach (var refusal in result.Refusals)
        {
            // No model content and no tenant tag on the counter; the tenant id is in this line.
            logger.LogWarning(
                "Restore of tenant '{TenantId}' refused before the attach ({Code}): service-managed CK model " +
                "'{CkModelId}' - {Reason}: {Detail}", tenantId, RefusalCode, refusal.ModelId?.ToString() ?? "-",
                ReasonTag(refusal.Reason!.Value), refusal.Detail);
            CkModelImportDiagnostics.RecordRestoreSystemModelRefused(ReasonTag(refusal.Reason!.Value));
        }

        return result;
    }

    private static RestoreSystemModelCheckResult Only(CkModelId? modelId, RestoreSystemModelRefusalReason reason,
        string detail) =>
        new([], [RestoreSystemModelDecision.Refuse(modelId, reason, detail)]);
}
