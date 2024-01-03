using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

internal class ModelLoaderService : IModelLoaderService
{
    private readonly ICkCacheService _cacheService;
    private readonly IModelResolver _modelResolver;

    public ModelLoaderService(ICkCacheService cacheService,
        IModelResolver modelResolver)
    {
        _cacheService = cacheService;
        _modelResolver = modelResolver;
    }

    public async Task LoadAsync(string tenantId, IOctoSession session, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        if (_cacheService.IsTenantLoaded(tenantId))
        {
            return;
        }

        OperationResult operationResult = new();
        var ckModels = await mongoDbRepositoryDataSource.CkModels.GetAsync(session);
        var modelGraph = await _modelResolver.ResolveAsync(ckModels.Select(x => x.Id).ToList(), operationResult);
        _cacheService.CreateTenant(tenantId);
        _cacheService.LoadCkModelGraph(tenantId, modelGraph);
    }
}