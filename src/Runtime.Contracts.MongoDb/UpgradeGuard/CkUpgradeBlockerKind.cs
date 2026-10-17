namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;

/// <summary>
///     Why an installed model would stop working when a CK model update is imported (tenant upgrade guard, AB#6867).
/// </summary>
public enum CkUpgradeBlockerKind
{
    /// <summary>
    ///     The dependent is exact-pinned (classic model, no dependency ranges) to a version of the updated model that the
    ///     update replaces: after the import the pin is unsatisfied and the dependent turns <c>ResolveFailed</c>.
    /// </summary>
    ExactPin,

    /// <summary>The updated version is below the floor of the dependent's range (a downgrade below what the dependent needs).</summary>
    FloorNotMet,

    /// <summary>
    ///     The dependent's range does not admit the updated version (for example an upper bound below it): after the
    ///     import the range is unsatisfied.
    /// </summary>
    RangeNotSatisfied,

    /// <summary>An element or member the dependent uses (<c>usedSurface</c> or a reference) is no longer defined by the update.</summary>
    ElementRemoved,

    /// <summary>An element the dependent uses is changed incompatibly by the update (a Major change of the classifier).</summary>
    ElementChanged,

    /// <summary>An element the dependent uses is internal in the update.</summary>
    ElementBecameInternal,

    /// <summary>A member the update newly declares collides with a member of a type of the dependent that derives from it.</summary>
    NameCollision
}
