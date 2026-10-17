using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UpgradeGuard;

/// <summary>
///     Reads the installed CK models of a tenant from MongoDB (AB#6867): one query for the model rows, and
///     <see cref="IDatabaseCkModelRepository.TryLookupCkModelAsync" /> for the models that need the full compiled form.
///     Read-only; a session is opened per call and never written through.
/// </summary>
internal sealed class MongoInstalledCkModelReader : IInstalledCkModelReader
{
    private readonly IDatabaseCkModelRepository _repository;
    private readonly TenantDatabaseSourceIdentifier _source;

    public MongoInstalledCkModelReader(IDatabaseCkModelRepository repository, TenantDatabaseSourceIdentifier source)
    {
        _repository = repository;
        _source = source;
    }

    public async Task<IReadOnlyList<CkModel>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        using var session = await _source.MongoDbRepositoryDataSource.CreateSessionAsync();
        var models = await _source.MongoDbRepositoryDataSource.CkModels.FindManyAsync(session,
            m => m.ModelState != ModelState.Importing);
        return models.ToList();
    }

    public Task<CkCompiledModelRoot?> LoadAsync(CkModelId modelId, CancellationToken cancellationToken) =>
        _repository.TryLookupCkModelAsync(modelId, new OperationResult(), _source, cancellationToken);
}
