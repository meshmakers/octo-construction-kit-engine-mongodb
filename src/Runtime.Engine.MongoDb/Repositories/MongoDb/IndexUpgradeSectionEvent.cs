namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>AB#9302: a stage of one lock section of the tiebreak index upgrade (tests only).</summary>
internal enum IndexUpgradeSectionStage
{
    /// <summary><c>index_update_lock</c> is held and the CK metadata re-read; the collection is next.</summary>
    Acquired,

    /// <summary>The collection run began tracking index states (inside the section); nothing is built yet.</summary>
    TrackingStarted,

    /// <summary>The collection is done (index states persisted, tracking ended); the lock is still held.</summary>
    Releasing,

    /// <summary>
    ///     The section disposed its lock: released, or - when the release failed (logged) or the lock was lost - left to
    ///     expire. Tests that hand the lock to another holder check the lock document themselves.
    /// </summary>
    Released,

    /// <summary>The upgrade checked for a waiting index update and yielded the lock if there was one.</summary>
    Yielded,

    /// <summary>
    ///     The collection holds a type of a model that is still being imported after its retry; it is left for the next
    ///     run (no index of it was dropped or replaced).
    /// </summary>
    LeftForNextRun
}

/// <summary>
///     AB#9302: one stage of a lock section of the tiebreak index upgrade, reported to
///     <c>MongoDbRepositoryDataSource.UpgradeSectionObserver</c> (tests only).
/// </summary>
/// <param name="Stage">The stage.</param>
/// <param name="CollectionName">The collection of the section; null for the last section (model-independent indexes).</param>
/// <param name="Section">The number of the section in the run, from 0.</param>
/// <param name="LockLostToken">The lock-lost token of the section's lock (none after the release).</param>
/// <param name="TrackingActive">True when an index state tracking scope is open at this stage.</param>
/// <param name="WaiterSeen">Stage <see cref="IndexUpgradeSectionStage.Yielded" />: a fresh waiter marker was seen.</param>
internal sealed record IndexUpgradeSectionEvent(
    IndexUpgradeSectionStage Stage,
    string? CollectionName,
    int Section,
    CancellationToken LockLostToken,
    bool TrackingActive,
    bool WaiterSeen);
