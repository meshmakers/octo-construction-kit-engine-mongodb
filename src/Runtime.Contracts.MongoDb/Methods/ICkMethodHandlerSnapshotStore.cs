namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;

/// <summary>
///     Durable snapshot of the CK method handler registry (AB#5671, F3.2) in the SYSTEM database, so a restarted
///     dispatcher host can answer a call without waiting for the next heartbeat round. Optional for a consumer: a
///     host that does not resolve it keeps the registry in memory only.
/// </summary>
/// <remarks>
///     The rows are a "last seen" record (see <see cref="CkMethodHandlerSnapshotRecord" />). Several dispatcher
///     replicas share the collection; an upsert never replaces a row with an older <c>LastSeenUtc</c>.
/// </remarks>
public interface ICkMethodHandlerSnapshotStore
{
    /// <summary>
    ///     Reads all rows. Returns an empty list when nothing was stored yet or the system database does not exist; it
    ///     never creates the database.
    /// </summary>
    Task<IReadOnlyList<CkMethodHandlerSnapshotRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Upserts rows by (<c>MethodKey</c>, <c>InstanceId</c>); a row whose stored <c>LastSeenUtc</c> is newer or equal
    ///     is kept. Does nothing and returns <c>false</c> while the system tenant does not exist yet (the write must not
    ///     materialize the system database before its bootstrap, AB#4854) or while the unique-key / TTL indexes cannot be
    ///     ensured; the caller retries later.
    /// </summary>
    /// <returns><c>true</c> when the rows were written, <c>false</c> when they were deferred.</returns>
    Task<bool> UpsertAsync(IReadOnlyCollection<CkMethodHandlerSnapshotRecord> records,
        CancellationToken cancellationToken = default);
}
