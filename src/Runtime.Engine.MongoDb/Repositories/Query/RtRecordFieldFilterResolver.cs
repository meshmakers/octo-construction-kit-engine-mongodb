using System.Collections;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Formulas;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class RtRecordFieldFilterResolver<TEntity> : RtFieldFilterResolver<TEntity>
    where TEntity : RtRecord, new()
{
    private readonly CkRecordGraph _ckRecordGraph;

    public RtRecordFieldFilterResolver(ICkCacheService ckCacheService, string tenantId, CkRecordGraph ckRecordGraph)
        : base(ckCacheService, tenantId, ckRecordGraph)
    {
        _ckRecordGraph = ckRecordGraph;
    }

    internal override string GetEntityName()
    {
        return _ckRecordGraph.CkRecordId.FullName;
    }
}