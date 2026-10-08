using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Engine.ModelRepositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public interface IDatabaseCkModelRepository : IModelRepository
{
    /// <summary>
    ///     Re-validates every installed CK model without importing anything (CK v2 F1.0-S2, AB#5901): <c>Available</c>
    ///     models that no longer resolve become <c>ResolveFailed</c>, <c>ResolveFailed</c> models that resolve again
    ///     become <c>Available</c> (their collections and indexes are restored). Runs in its own transaction, retried
    ///     on transient errors. The same pass runs at the end of every import.
    /// </summary>
    /// <returns>The models that returned to <c>Available</c>.</returns>
    Task<IReadOnlyCollection<CkModelId>> RevalidateAsync(object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null);
}
