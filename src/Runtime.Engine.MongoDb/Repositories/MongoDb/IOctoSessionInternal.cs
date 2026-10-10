using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal interface IOctoSessionInternal
{
    IClientSessionHandle SessionHandle { get; }

    /// <summary>
    ///     True while a transaction was started and is neither committed nor aborted.
    /// </summary>
    bool IsTransactionActive { get; }

    /// <summary>
    ///     Registers work for side effects that live outside the Mongo transaction (e.g. GridFS bytes).
    ///     <paramref name="afterCommit" /> runs only after the transaction has committed successfully;
    ///     <paramref name="afterRollback" /> runs when the transaction is aborted or the session is disposed
    ///     without a commit. Failures are logged and never thrown. Must only be called while
    ///     <see cref="IsTransactionActive" /> is true.
    /// </summary>
    void RegisterTransactionCallbacks(Func<Task>? afterCommit, Func<Task>? afterRollback = null);
}
