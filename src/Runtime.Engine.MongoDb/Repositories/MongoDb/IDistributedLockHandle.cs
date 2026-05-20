namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Handle to a held distributed lock.
/// Disposing the handle releases the lock.
/// </summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>
    /// Signaled when ownership of the lock has been lost — typically because another
    /// service claimed it after our TTL expired (e.g. due to a GC pause, DB stall,
    /// or network partition that kept our heartbeat from updating in time).
    /// Long-running operations that hold this lock should observe this token and
    /// abort their work to prevent split-brain writes against shared state.
    /// </summary>
    CancellationToken LockLostToken { get; }
}
