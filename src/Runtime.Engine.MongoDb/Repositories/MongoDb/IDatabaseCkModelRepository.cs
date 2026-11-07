using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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

    /// <summary>
    ///     Validates a compiled CK model against the models installed in the tenant without importing it (AB#8492): the
    ///     pre-checks of the import (CK language, <c>minEngineVersion</c>, system model references) and the resolution of
    ///     its dependencies and elements against the installed models. Strictly read-only. Every finding is added to
    ///     <paramref name="operationResult" />; a missing dependency is an error message, never an exception.
    /// </summary>
    Task ValidateModelAsync(CkCompiledModelRoot ckCompiledModel, OperationResult operationResult,
        object? sourceIdentifier = null);
}
