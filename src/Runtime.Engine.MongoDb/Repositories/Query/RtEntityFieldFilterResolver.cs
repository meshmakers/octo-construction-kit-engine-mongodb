using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;


internal class RtEntityFieldFilterResolver<TEntity>(
    ICkCacheService ckCacheService,
    string tenantId,
    CkTypeGraph ckTypeGraph)
    : RtFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph)
    where TEntity : RtEntity, new()
{
    internal override string GetEntityName()
    {
        return ckTypeGraph.CkTypeId.FullName;
    }
    
    internal override bool IsAttributeNameValid(string attributeName)
    {
        return ckTypeGraph.AllAttributesByName.ContainsKey(attributeName) ||
               attributeName == nameof(RtEntity.RtId) ||
               attributeName == nameof(RtEntity.RtCreationDateTime) ||
               attributeName == nameof(RtEntity.RtChangedDateTime) ||
               attributeName == nameof(RtEntity.RtWellKnownName);
    }
    
    internal override string ResolveAttributeName(string attributeName)
    {
        if (typeof(RtEntity).GetProperty(attributeName) != null)
        {
            return attributeName.ToCamelCase();
        }
        
        var baseResolve = base.ResolveAttributeName(attributeName);
        if (!string.IsNullOrEmpty(baseResolve))
        {
            return baseResolve;
        }

        return $"{Constants.AttributesName}.{attributeName.ToCamelCase()}";
    }
}