using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

internal class ModelLoaderService : IModelLoaderService
{
    private readonly ICkCacheService _cacheService;
    private readonly IModelResolver _modelResolver;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

    public ModelLoaderService(ICkCacheService cacheService,
        IModelResolver modelResolver)
    {
        _cacheService = cacheService;
        _modelResolver = modelResolver;
    }

    public async Task LoadAsync(string tenantId, IOctoSession session, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        // This ensures that a tenant is only loaded once - not in parallel.
        if (_cacheService.IsTenantLoaded(tenantId))
        {
            return;
        }

        await _loadSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_cacheService.IsTenantLoaded(tenantId))
            {
                return;
            }
            
            var sourceIdentifier = new TenantDatabaseSourceIdentifier(mongoDbRepositoryDataSource);
            OperationResult operationResult = new();
            var ckModels = await mongoDbRepositoryDataSource.CkModels.FindManyAsync(session, m=> m.ModelState == ModelState.Available);
            var modelGraph = await _modelResolver.ResolveAsync(ckModels.Select(x => x.Id).ToList(), operationResult, sourceIdentifier);
            _cacheService.CreateTenant(tenantId);
            _cacheService.LoadCkModelGraph(tenantId, modelGraph);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }
}