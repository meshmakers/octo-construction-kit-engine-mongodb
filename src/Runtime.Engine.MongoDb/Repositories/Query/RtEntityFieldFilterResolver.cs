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
    
    internal override bool IsAttributePathValid(string attributePath)
    {
        if (base.IsAttributePathValid(attributePath))
        {
            return true;
        }

        return attributePath == nameof(RtEntity.RtId) ||
               attributePath == nameof(RtEntity.RtCreationDateTime) ||
               attributePath == nameof(RtEntity.RtChangedDateTime) ||
               attributePath == nameof(RtEntity.RtVersion) ||
               attributePath == nameof(RtEntity.RtWellKnownName);
    }
    
    internal override string? ResolveAttributePath(string attributePath)
    {
        var r = base.ResolveAttributePath(attributePath);
        if (!string.IsNullOrWhiteSpace(r))
        {
            return r;
        }

        return attributePath switch
        {
            nameof(RtEntity.RtId) => Constants.IdField,
            nameof(RtEntity.RtCreationDateTime) => nameof(RtEntity.RtCreationDateTime).ToCamelCase(),
            nameof(RtEntity.RtChangedDateTime) => nameof(RtEntity.RtChangedDateTime).ToCamelCase(),
            nameof(RtEntity.RtVersion) => nameof(RtEntity.RtVersion).ToCamelCase(),
            nameof(RtEntity.RtWellKnownName) => nameof(RtEntity.RtWellKnownName).ToCamelCase(),
            _ => null
        };
    }
}
