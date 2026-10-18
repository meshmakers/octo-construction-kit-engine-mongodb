using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Engine.SemVer;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UpgradeGuard;

/// <summary>
///     Default implementation of <see cref="ITenantUpgradeGuard" /> (CK v2 F2.5, AB#6867).
///     <para>
///         The update set is judged as the <b>post-state</b>: every installed <c>Available</c> model that the set does not
///         replace, plus every model of the set, is a dependent of every <i>replacement</i> in the set. A replacement is a
///         candidate whose name is installed in the same major with another version. For each (replacement, dependent):
///     </para>
///     <list type="bullet">
///         <item>exact-pinned dependent (no <c>dependencyRanges</c>): a pin on another version of the same major is an
///             <see cref="CkUpgradeBlockerKind.ExactPin" /> blocker; the pin on the candidate's own version is fine
///             (this is how a replaced dependent in the set stops being a blocker);</item>
///         <item>range-retaining dependent: the engine's <see cref="ICkSurfaceSatisfactionChecker" /> judges range, floor,
///             every referenced element and every <c>usedSurface</c> member; its findings are mapped to blocker kinds;</item>
///         <item>never dependents: models that are <c>ResolveFailed</c> or being imported; never replacements: service-managed
///             models (<c>System</c>, <c>System.*</c>) unless the caller asks for them (explicit imports, AB#6966),
    ///             candidates of another major than the installed one (a parallel
///             install) and candidates that are not installed.</item>
///     </list>
///     Uses persisted data only: the dependents come from the tenant's database, never from a catalog.
/// </summary>
public class TenantUpgradeGuard : ITenantUpgradeGuard
{
    private readonly IDatabaseCkModelRepository _repository;
    private readonly ICkModelDiffService _diffService;
    private readonly ICkSemVerClassifier _classifier;
    private readonly ICkSurfaceSatisfactionChecker _checker;
    private readonly ILogger<TenantUpgradeGuard> _logger;

    /// <summary>
    ///     Creates a new instance of the <see cref="TenantUpgradeGuard" /> class.
    /// </summary>
    public TenantUpgradeGuard(IDatabaseCkModelRepository repository, ICkModelDiffService diffService,
        ICkSemVerClassifier classifier, ICkSurfaceSatisfactionChecker checker, ILogger<TenantUpgradeGuard> logger)
    {
        _repository = repository;
        _diffService = diffService;
        _classifier = classifier;
        _checker = checker;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<CkUpgradeGuardResult> EvaluateAsync(TenantDatabaseSourceIdentifier source,
        IReadOnlyCollection<CkCompiledModelRoot> updateSet,
        IReadOnlyCollection<CkModelId>? availableDependentVersions = null,
        CancellationToken cancellationToken = default, bool includeServiceManagedModels = false) =>
        EvaluateAsync(new MongoInstalledCkModelReader(_repository, source), updateSet, availableDependentVersions,
            cancellationToken, includeServiceManagedModels);

    internal async Task<CkUpgradeGuardResult> EvaluateAsync(IInstalledCkModelReader reader,
        IReadOnlyCollection<CkCompiledModelRoot> updateSet, IReadOnlyCollection<CkModelId>? availableDependentVersions,
        CancellationToken cancellationToken, bool includeServiceManagedModels = false)
    {
        ArgumentNullException.ThrowIfNull(updateSet);
        if (updateSet.Count == 0)
        {
            return CkUpgradeGuardResult.None;
        }

        var installed = await reader.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        var replacements = FindReplacements(updateSet, installed, includeServiceManagedModels);
        if (replacements.Count == 0)
        {
            return CkUpgradeGuardResult.None;
        }

        var replacedNames = replacements.Select(r => r.Installed.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependents = installed
            .Where(m => m.ModelState == ModelState.Available && !replacedNames.Contains(m.Id.Name))
            .Select(m => new Dependent(m.Id, m.Dependencies ?? [], m.DependencyRanges?.Select(r => r.ToDto()).ToList(),
                null, false))
            .Concat(updateSet.Where(c => includeServiceManagedModels || !IsServiceManaged(c.ModelId.Name))
                .Select(c => new Dependent(c.ModelId, c.Dependencies ?? [], c.DependencyRanges, c, true)))
            .ToList();

        var blockers = new List<CkUpgradeBlocker>();
        var notes = new List<string>();
        var loaded = new Dictionary<CkModelId, CkCompiledModelRoot?>();

        foreach (var (candidate, installedModel) in replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateId = candidate.ModelId;
            var related = dependents
                .Where(d => !string.Equals(d.Id.Name, candidateId.Name, StringComparison.OrdinalIgnoreCase))
                .Select(d => (Dependent: d, Range: d.FindRange(candidateId.Name), Pin: d.FindPin(candidateId.Name)))
                .Where(x => x.Range != null || x.Pin != null)
                .ToList();

            var rangeDependents = new List<(Dependent Dependent, CkCompiledModelRoot Root)>();
            foreach (var (dependent, range, pin) in related)
            {
                if (range == null)
                {
                    AddExactPinBlocker(blockers, dependent, pin!, candidateId, availableDependentVersions);
                    continue;
                }

                var root = dependent.Root ?? await LoadOnceAsync(reader, loaded, dependent.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (root == null)
                {
                    notes.Add($"{dependent.Id.FullName} could not be loaded from the tenant and was not checked against " +
                              $"{candidateId.FullName}.");
                    continue;
                }

                if (range.UsedSurface == null)
                {
                    notes.Add($"{dependent.Id.FullName} has no usedSurface for {candidateId.Name} (compiled before " +
                              "AB#4472): only its referenced elements were checked, not the members it uses.");
                }

                rangeDependents.Add((dependent, root));
            }

            if (rangeDependents.Count == 0)
            {
                continue;
            }

            var baseline = await LoadOnceAsync(reader, loaded, installedModel.Id, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ConstructionKit.Contracts.SemVer.CkClassifiedModelChange> classified = [];
            if (baseline == null)
            {
                notes.Add($"The installed {installedModel.Id.FullName} could not be loaded: changes of {candidateId.FullName} " +
                          "could not be classified, only existence and visibility of the used elements were checked.");
            }
            else
            {
                var changes = _diffService.Diff(baseline, candidate);
                classified = _classifier.Classify(changes, baseline, candidate);
            }

            var surfaceCandidate = CkSurfaceCandidate.Create(candidate, baseline, classified,
                rangeDependents.Select(r => r.Root).ToList());
            foreach (var (dependent, root) in rangeDependents)
            {
                var check = _checker.Check(surfaceCandidate, root);
                AddCheckBlockers(blockers, dependent, check, candidateId, availableDependentVersions);
            }
        }

        var ordered = blockers
            .DistinctBy(b => (b.Dependent, b.Update, b.Kind, b.Subject, b.Message))
            .OrderBy(b => b.Dependent.Name, StringComparer.Ordinal).ThenBy(b => b.Dependent.Version)
            .ThenBy(b => b.Update.Name, StringComparer.Ordinal).ThenBy(b => b.Kind).ThenBy(b => b.Subject, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count > 0)
        {
            _logger.LogInformation("Upgrade guard: the update set [{UpdateSet}] would break {Count} blocker(s) of {Dependents}",
                string.Join(", ", updateSet.Select(c => c.ModelId.FullName)), ordered.Count,
                string.Join(", ", ordered.Select(b => b.Dependent.FullName).Distinct()));
        }

        return new CkUpgradeGuardResult(ordered, notes.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    ///     True for the models a service manages itself (<c>System</c>, <c>System.*</c>): the downgrade guard of the
    ///     embedded import and the service version decide there, not the upgrade guard.
    /// </summary>
    internal static bool IsServiceManaged(string modelName) =>
        string.Equals(modelName, "System", StringComparison.OrdinalIgnoreCase) ||
        modelName.StartsWith("System.", StringComparison.OrdinalIgnoreCase);

    private static List<(CkCompiledModelRoot Candidate, CkModel Installed)> FindReplacements(
        IReadOnlyCollection<CkCompiledModelRoot> updateSet, IReadOnlyList<CkModel> installed,
        bool includeServiceManagedModels)
    {
        var result = new List<(CkCompiledModelRoot, CkModel)>();
        foreach (var candidate in updateSet)
        {
            var id = candidate.ModelId;
            if (!includeServiceManagedModels && IsServiceManaged(id.Name))
            {
                continue;
            }

            var current = installed.FirstOrDefault(m =>
                string.Equals(m.Id.Name, id.Name, StringComparison.OrdinalIgnoreCase) &&
                m.Id.Version.Major == id.Version.Major && m.ModelState != ModelState.Importing);
            // Same version: nothing changes. ResolveFailed installed: it cannot be broken by the update, and the
            // update may even repair it. Another major (or not installed): not a replacement.
            if (current == null || current.ModelState != ModelState.Available || current.Id.Version.CompareTo(id.Version) == 0)
            {
                continue;
            }

            result.Add((candidate, current));
        }

        return result;
    }

    private static async Task<CkCompiledModelRoot?> LoadOnceAsync(IInstalledCkModelReader reader,
        Dictionary<CkModelId, CkCompiledModelRoot?> cache, CkModelId id, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(id, out var model))
        {
            model = await reader.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            cache[id] = model;
        }

        return model;
    }

    private static void AddExactPinBlocker(List<CkUpgradeBlocker> blockers, Dependent dependent, CkModelId pin,
        CkModelId update, IReadOnlyCollection<CkModelId>? available)
    {
        // A pin on another major is a different (parallel) installation: not affected.
        if (pin.Version.Major != update.Version.Major || pin.Version.CompareTo(update.Version) == 0)
        {
            return;
        }

        blockers.Add(Blocker(dependent, update, CkUpgradeBlockerKind.ExactPin, pin.FullName,
            $"{dependent.Id.FullName}: exact pin {pin.FullName}, but the update replaces it with {update.FullName}",
            available));
    }

    private static void AddCheckBlockers(List<CkUpgradeBlocker> blockers, Dependent dependent, CkDependentCheck check,
        CkModelId update, IReadOnlyCollection<CkModelId>? available)
    {
        var rangeName = dependent.FindRange(update.Name)?.Range.FullName ?? update.Name;
        switch (check.Verdict)
        {
            case CkDependentVerdict.NotInRange:
                // The dependent was Available, so its range admitted the installed version; it does not admit the
                // candidate (an upper bound below it): after the import the range is unsatisfied.
                blockers.AddRange(check.Reasons.Select(reason => Blocker(dependent, update,
                    CkUpgradeBlockerKind.RangeNotSatisfied, rangeName, reason, available)));
                break;
            case CkDependentVerdict.Breaks:
                blockers.AddRange(check.Reasons.Select(reason =>
                {
                    var kind = Classify(reason);
                    return Blocker(dependent, update, kind, kind == CkUpgradeBlockerKind.FloorNotMet ? rangeName : Subject(reason),
                        reason, available);
                }));
                break;
            case CkDependentVerdict.NeedsRepin:
                // A range-retaining dependent never needs a re-pin. An exact-pinned one is judged by the pin rule.
            case CkDependentVerdict.Compatible:
            default:
                break;
        }
    }

    private static CkUpgradeBlocker Blocker(Dependent dependent, CkModelId update, CkUpgradeBlockerKind kind,
        string subject, string message, IReadOnlyCollection<CkModelId>? available) =>
        new(dependent.Id, update, kind, subject, CodeOf(kind), message, dependent.IsInUpdateSet,
            dependent.IsInUpdateSet ? null : Suggest(dependent.Id, available));

    private static CkModelId? Suggest(CkModelId dependent, IReadOnlyCollection<CkModelId>? available) =>
        available?
            .Where(a => string.Equals(a.Name, dependent.Name, StringComparison.OrdinalIgnoreCase) &&
                        a.Version.CompareTo(dependent.Version) > 0)
            .OrderByDescending(a => a.Version)
            .FirstOrDefault();

    internal static string CodeOf(CkUpgradeBlockerKind kind) => kind switch
    {
        CkUpgradeBlockerKind.ExactPin => "CK_UPGRADE_EXACT_PIN",
        CkUpgradeBlockerKind.FloorNotMet => "CK_UPGRADE_FLOOR_NOT_MET",
        CkUpgradeBlockerKind.RangeNotSatisfied => "CK_UPGRADE_RANGE_NOT_SATISFIED",
        CkUpgradeBlockerKind.ElementRemoved => "CK_UPGRADE_ELEMENT_REMOVED",
        CkUpgradeBlockerKind.ElementChanged => "CK_UPGRADE_ELEMENT_CHANGED",
        CkUpgradeBlockerKind.ElementBecameInternal => "CK_UPGRADE_ELEMENT_BECAME_INTERNAL",
        CkUpgradeBlockerKind.NameCollision => "CK_UPGRADE_NAME_COLLISION",
        _ => "CK_UPGRADE_BLOCKED"
    };

    /// <summary>
    ///     Maps a finding of <see cref="CkSurfaceSatisfactionChecker" /> to a blocker kind. The checker reports findings
    ///     as texts with fixed phrases; <c>TenantUpgradeGuardTests</c> run the real checker against every phrase, so a
    ///     rewording in the engine turns a test red instead of silently changing the kind (open point: structured findings
    ///     in the engine's <c>CkDependentCheck</c>).
    /// </summary>
    internal static CkUpgradeBlockerKind Classify(string reason)
    {
        if (reason.StartsWith("floor not met", StringComparison.Ordinal))
        {
            return CkUpgradeBlockerKind.FloorNotMet;
        }

        if (reason.Contains(", which changes incompatibly", StringComparison.Ordinal))
        {
            return CkUpgradeBlockerKind.ElementChanged;
        }

        if (reason.Contains(", which is internal in ", StringComparison.Ordinal))
        {
            return CkUpgradeBlockerKind.ElementBecameInternal;
        }

        if (reason.Contains(", which ", StringComparison.Ordinal) && reason.Contains(" does not define ", StringComparison.Ordinal) ||
            reason.Contains(" is no longer declared by ", StringComparison.Ordinal))
        {
            return CkUpgradeBlockerKind.ElementRemoved;
        }

        if (reason.Contains("name collision", StringComparison.Ordinal) ||
            reason.Contains("duplicate method id", StringComparison.Ordinal))
        {
            return CkUpgradeBlockerKind.NameCollision;
        }

        return CkUpgradeBlockerKind.ElementChanged;
    }

    private static readonly Regex UsesSubject = new(@"\suses\s(?<subject>[^\s,:]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The element or member a finding is about (<c>Basic-2.5.0/Asset-1</c>); a name collision names the colliding base type.</summary>
    internal static string Subject(string reason)
    {
        var match = UsesSubject.Match(reason);
        if (match.Success)
        {
            return match.Groups["subject"].Value;
        }

        var space = reason.IndexOf(' ');
        return space > 0 ? reason[..space] : reason;
    }

    private sealed record Dependent(CkModelId Id, IReadOnlyList<CkModelId> Pins,
        IReadOnlyList<CkModelDependencyDto>? Ranges, CkCompiledModelRoot? Root, bool IsInUpdateSet)
    {
        public CkModelDependencyDto? FindRange(string name) =>
            Ranges?.FirstOrDefault(r => string.Equals(r.Range.Name, name, StringComparison.OrdinalIgnoreCase));

        public CkModelId? FindPin(string name) =>
            Ranges != null
                ? null
                : Pins.FirstOrDefault(p => !p.IsMajorQualified && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
