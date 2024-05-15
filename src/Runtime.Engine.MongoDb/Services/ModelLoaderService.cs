using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

internal class ModelLoaderService(
    ICkCacheService cacheService,
    IModelResolver modelResolver)
    : IModelLoaderService
{
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

    public async Task LoadAsync(string tenantId, IOctoSession session, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        // This ensures that a tenant is only loaded once - not in parallel.
        if (cacheService.IsTenantLoaded(tenantId))
        {
            return;
        }

        await _loadSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (cacheService.IsTenantLoaded(tenantId))
            {
                return;
            }
            
            var sourceIdentifier = new TenantDatabaseSourceIdentifier(mongoDbRepositoryDataSource);
            OperationResult operationResult = new();
            var ckModels = await mongoDbRepositoryDataSource.CkModels.FindManyAsync(session, m=> m.ModelState == ModelState.Available);
            var modelGraph = await modelResolver.ResolveAsync(ckModels.Select(x => x.Id).ToList(), operationResult, sourceIdentifier);
            cacheService.CreateTenant(tenantId);
            cacheService.LoadCkModelGraph(tenantId, modelGraph);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }
}