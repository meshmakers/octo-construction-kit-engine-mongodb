using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class ModelLoaderService : IModelLoaderService
{
    private readonly ICkModelRepositoryService _ckModelRepositoryService;
    private readonly ICkCacheService _cacheService;
    private readonly IModelResolver _modelResolver;

    public ModelLoaderService(ICkModelRepositoryService ckModelRepositoryService, ICkCacheService cacheService, IModelResolver modelResolver)
    {
        _ckModelRepositoryService = ckModelRepositoryService;
        _cacheService = cacheService;
        _modelResolver = modelResolver;
    }

    public async Task LoadAsync(string tenantId, IOctoSession session, IDatabaseContext databaseContext)
    {
        if (_cacheService.IsTenantLoaded(tenantId))
        {
            return;
        }
        
        
        OperationResult operationResult = new();


        var ckModels = await databaseContext.CkModels.GetAsync(session);
        CkModelGraph modelGraph = await _modelResolver.ResolveAsync(ckModels.Select(x=> x.Id).ToList(), operationResult);
        // foreach (var ckModel in ckModels)
        // {
        //     OperationResult operationResult = new();
        //     var compiledModelRoot = await _ckModelRepositoryService.LookupCkModelAsync(ckModel.Id, operationResult,
        //         new TenantDatabaseSourceIdentifier(databaseContext, session));
        //     if (compiledModelRoot == null)
        //     {
        //         throw new Exception("Very bad");
        //     }
        //     modelGraph.AppendModel(compiledModelRoot);
        // }

        _cacheService.CreateTenant(tenantId);
        _cacheService.LoadCkModelGraph(tenantId, modelGraph);

    }
    
}