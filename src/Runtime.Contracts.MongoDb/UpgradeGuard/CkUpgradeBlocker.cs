using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;

/// <summary>
///     One reason why an installed model (or a model of the update set itself) would break if the update set were
///     imported (tenant upgrade guard, AB#6867). Language-neutral: <see cref="Code" />, <see cref="Dependent" />,
///     <see cref="Update" /> and <see cref="Subject" /> are the data a presentation layer renders;
///     <see cref="Message" /> is an English text for logs and for clients that do not localize.
/// </summary>
/// <param name="Dependent">The model that would break (its installed id, or its id in the update set).</param>
/// <param name="Update">The model of the update set that causes it.</param>
/// <param name="Kind">The kind of the blocker.</param>
/// <param name="Subject">
///     The pin (<c>Basic-2.4.0</c>), the range (<c>Basic-[2.4,3.0)</c>) or the element / member id
///     (<c>Basic@2/Asset-1</c>, <c>Basic@2/Asset-1.Name</c>) the blocker is about.
/// </param>
/// <param name="Code">Stable code per kind, for example <c>CK_UPGRADE_EXACT_PIN</c>.</param>
/// <param name="Message">English explanation, for example <c>exact pin Basic-2.4.0: Basic-2.5.0 replaces it</c>.</param>
/// <param name="DependentIsInUpdateSet">True when <paramref name="Dependent" /> is itself a model of the update set.</param>
/// <param name="SuggestedDependentUpdate">
///     A newer version of the dependent (from the versions the caller offered) that may resolve the blocker, for
///     example <c>EnergyCommunity.Registration-2.10.0</c>; <c>null</c> when none was offered. It is a suggestion: the
///     caller proves it by evaluating the combined update set.
/// </param>
public sealed record CkUpgradeBlocker(
    CkModelId Dependent,
    CkModelId Update,
    CkUpgradeBlockerKind Kind,
    string Subject,
    string Code,
    string Message,
    bool DependentIsInUpdateSet = false,
    CkModelId? SuggestedDependentUpdate = null);
