using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class RtRecordFieldFilterResolver<TEntity>(
    ICkCacheService ckCacheService,
    string tenantId,
    CkRecordGraph ckRecordGraph)
    : RtFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckRecordGraph)
    where TEntity : RtRecord, new()
{
    internal override string GetEntityName()
    {
        return ckRecordGraph.CkRecordId.FullName;
    }
}