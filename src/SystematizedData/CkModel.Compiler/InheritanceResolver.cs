using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public class InheritanceResolver
{
    private readonly ILogger<InheritanceResolver> _logger;

    public InheritanceResolver(ILogger<InheritanceResolver> logger)
    {
        _logger = logger;
    }
    //
    // public async Task ResolveInheritanceAsync(CkAggregatedModelElements aggregatedModelElements)
    // {
    //     _logger.LogInformation("Starting resolving inheritance");
    //     
    //     CkDependencyGraph dependencyGraph = new(aggregatedModelElements);
    //     
    //     foreach (var entityCacheItem in aggregatedModelElements.CkEntities.Values)
    //     {
    //         dependencyGraph.GetBaseTypes()
    //      //   BuildInheritanceGraph(entityCacheItem.CkTypeInfo, entityCacheItem);
    //     }
    //
    // }

    private void BuildInheritance()
    {
        
    }
    
    
    // private void BuildInheritanceGraph(CkTypeInfo ckTypeInfo, EntityCacheItem entityCacheItem)
    // {
    //     _logger.LogDebug("Building inheritance graph '{CkId}'", entityCacheItem.CkId);
    //
    //     foreach (var ckBaseTypeInfo in ckTypeInfo.BaseTypes)
    //     {
    //         if (ckBaseTypeInfo.OriginCkId.Equals(entityCacheItem.CkId))
    //         {
    //             continue;
    //         }
    //
    //         _logger.LogDebug("Building inheritance graph '{CkId}', base type '{OriginCkId}'",entityCacheItem.CkId, ckBaseTypeInfo.OriginCkId);
    //         var baseType = _metaCache[ckBaseTypeInfo.OriginCkId];
    //         if (ckBaseTypeInfo.TargetCkId.Equals(entityCacheItem.CkId))
    //         {
    //             entityCacheItem.BaseType = baseType;
    //         }
    //
    //         baseType.DerivedTypes.Add(entityCacheItem);
    //     }
    // }
}