namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;

/// <summary>
///     The outcome of a tenant upgrade guard evaluation (AB#6867): which installed models an update set would break.
///     Read-only information; nothing has been written.
/// </summary>
/// <param name="Blockers">The blockers, ordered by dependent, update and kind; empty when the update set breaks nothing.</param>
/// <param name="Notes">
///     Things the guard could not prove (a dependent that could not be loaded, a model without <c>usedSurface</c>),
///     English texts. A note is not a blocker: the guard did not find a problem, but could not look everywhere.
/// </param>
public sealed record CkUpgradeGuardResult(
    IReadOnlyList<CkUpgradeBlocker> Blockers,
    IReadOnlyList<string> Notes)
{
    /// <summary>A result without blockers and notes.</summary>
    public static CkUpgradeGuardResult None { get; } = new([], []);

    /// <summary>True when at least one installed model would break.</summary>
    public bool IsBlocked => Blockers.Count > 0;
}
