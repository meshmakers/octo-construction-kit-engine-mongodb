using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>Why a tenant restore refuses a service-managed model of its dump. The tag value is the counter's <c>reason</c>.</summary>
internal enum RestoreSystemModelRefusalReason
{
    /// <summary>The catalog publishes this exact id with another content (a manipulated model).</summary>
    ContentDiffers,

    /// <summary>The catalog publishes this major of the model, but not this version.</summary>
    UnknownVersion,

    /// <summary>The version's major is higher than every published major of the model.</summary>
    HigherMajor,

    /// <summary>The catalog publishes no version of this model name at all (e.g. a fake <c>System.Foo</c>).</summary>
    UnknownModel,

    /// <summary>The version the model would end at is below the platform's security floor for it.</summary>
    BelowFloor,

    /// <summary>The platform's version of the model (system tenant) is not published with identical content.</summary>
    TargetNotPublished,

    /// <summary>The element rows of a service-managed model do not belong to its model row.</summary>
    InconsistentRows,

    /// <summary>The dump holds more than one version of the same service-managed model.</summary>
    MultipleVersions,

    /// <summary>The catalog could not be read; the check fails closed.</summary>
    CatalogUnavailable,

    /// <summary>The system tenant's models could not be read; the check fails closed.</summary>
    SystemTenantUnavailable,

    /// <summary>A service-managed model of the dump could not be read; the check fails closed.</summary>
    ModelUnreadable,

    /// <summary>Raising a genuine older model to the platform's version did not reach that version.</summary>
    UpgradeFailed
}

/// <summary>What the restore does with one service-managed model of its dump.</summary>
internal enum RestoreSystemModelAction
{
    /// <summary>Genuine and at (or above) the platform's version: kept as it is.</summary>
    Keep,

    /// <summary>Genuine but older than the platform's version of the same major: raised to that version.</summary>
    Raise,

    /// <summary>The restore is refused before the attach.</summary>
    Refuse
}

/// <summary>What the check knows about one service-managed model of a dump, gathered before the decision.</summary>
/// <param name="DumpModelId">The model id the restored database holds.</param>
/// <param name="PublishedWithSameId">The catalog publishes exactly <paramref name="DumpModelId" />.</param>
/// <param name="ContentEqualsCatalog">
///     The restored model (model row and element rows) has the canonical content of the catalog's model of the same id;
///     meaningful only when <paramref name="PublishedWithSameId" /> holds.
/// </param>
/// <param name="HighestPublishedMajor">The highest major the catalog publishes of the model name; null when none.</param>
/// <param name="PlatformModelId">
///     The version the platform runs: the model of the same name installed in the system tenant; null when the system
///     tenant does not hold it.
/// </param>
/// <param name="PlatformModelPublishedIdentical">
///     The system tenant's model is published with identical content; meaningful only when
///     <paramref name="PlatformModelId" /> is set.
/// </param>
/// <param name="Floor">The platform's security floor of the model name; null when it has none.</param>
internal sealed record RestoreSystemModelFacts(
    CkModelId DumpModelId,
    bool PublishedWithSameId,
    bool ContentEqualsCatalog,
    int? HighestPublishedMajor,
    CkModelId? PlatformModelId,
    bool PlatformModelPublishedIdentical,
    CkVersion? Floor);

/// <summary>The decision for one service-managed model of a dump.</summary>
/// <param name="ModelId">The model id of the dump; null for a refusal that concerns no single model (rows, reads).</param>
/// <param name="Action">Keep, raise or refuse.</param>
/// <param name="Target">The version the model ends at (the dump's own id when kept); null on a refusal.</param>
/// <param name="Reason">The refusal reason; null unless <see cref="RestoreSystemModelAction.Refuse" />.</param>
/// <param name="Detail">One sentence for the refusal message; names versions only, never content.</param>
internal sealed record RestoreSystemModelDecision(
    CkModelId? ModelId,
    RestoreSystemModelAction Action,
    CkModelId? Target,
    RestoreSystemModelRefusalReason? Reason,
    string Detail)
{
    internal static RestoreSystemModelDecision Refuse(CkModelId? modelId, RestoreSystemModelRefusalReason reason,
        string detail) => new(modelId, RestoreSystemModelAction.Refuse, null, reason, detail);
}

/// <summary>
///     The decision table of the restore check of service-managed models (System, System.*). Pure, so every branch is
///     unit-tested without a database. Order of the rules:
///     <list type="number">
///         <item>Not published with this id: refused (unknown model, higher major than published, or unknown version).</item>
///         <item>Published with this id but another content: refused - also when the version is older; a manipulated
///         model is never raised.</item>
///         <item>Genuine and older than the platform's version (system tenant) of the same major: raised to exactly the
///         platform's version, which must itself be published with identical content.</item>
///         <item>Genuine and equal to or newer than the platform's version, of another major, or not held by the system
///         tenant: kept (a restore never downgrades).</item>
///         <item>Whatever is kept or raised must reach the security floor of the model name, else refused.</item>
///     </list>
/// </summary>
internal static class RestoreSystemModelDecider
{
    internal static RestoreSystemModelDecision Decide(RestoreSystemModelFacts facts)
    {
        var dump = facts.DumpModelId;
        if (!facts.PublishedWithSameId)
        {
            if (facts.HighestPublishedMajor == null)
            {
                return RestoreSystemModelDecision.Refuse(dump, RestoreSystemModelRefusalReason.UnknownModel,
                    $"the CK catalog publishes no version of '{dump.Name}'");
            }

            if (dump.Version.Major > facts.HighestPublishedMajor.Value)
            {
                return RestoreSystemModelDecision.Refuse(dump, RestoreSystemModelRefusalReason.HigherMajor,
                    $"major {dump.Version.Major} is higher than the highest published major " +
                    $"{facts.HighestPublishedMajor.Value} of '{dump.Name}'");
            }

            return RestoreSystemModelDecision.Refuse(dump, RestoreSystemModelRefusalReason.UnknownVersion,
                $"the CK catalog does not publish version {dump.Version} of '{dump.Name}'");
        }

        if (!facts.ContentEqualsCatalog)
        {
            return RestoreSystemModelDecision.Refuse(dump, RestoreSystemModelRefusalReason.ContentDiffers,
                "its content differs from the published model of the same id");
        }

        var target = dump;
        var platform = facts.PlatformModelId;
        if (platform != null && platform.Version.Major == dump.Version.Major &&
            platform.Version.CompareTo(dump.Version) > 0)
        {
            if (!facts.PlatformModelPublishedIdentical)
            {
                return RestoreSystemModelDecision.Refuse(dump, RestoreSystemModelRefusalReason.TargetNotPublished,
                    $"it is older than the platform's '{platform}', which is not published with identical content " +
                    "in the CK catalog, so the restore cannot raise it");
            }

            target = platform;
        }

        if (facts.Floor is { } floor && target.Version.CompareTo(floor) < 0)
        {
            return RestoreSystemModelDecision.Refuse(dump, RestoreSystemModelRefusalReason.BelowFloor,
                target == dump
                    ? $"version {dump.Version} is below the platform's security floor {facts.Floor} of '{dump.Name}'"
                    : $"the platform's '{target}' is below the security floor {facts.Floor} of '{dump.Name}'");
        }

        return target == dump
            ? new RestoreSystemModelDecision(dump, RestoreSystemModelAction.Keep, dump, null, "genuine")
            : new RestoreSystemModelDecision(dump, RestoreSystemModelAction.Raise, target, null,
                $"genuine, raised to the platform's '{target}'");
    }
}
