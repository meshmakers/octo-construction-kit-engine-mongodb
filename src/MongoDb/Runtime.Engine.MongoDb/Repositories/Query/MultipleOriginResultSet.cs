using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class MultipleOriginResultSet<TEntity> : Dictionary<OctoObjectId, IResultSet<TEntity>>,
    IMultipleOriginResultSet<TEntity>
{
    public MultipleOriginResultSet(List<QueryMultipleResult<TEntity>> queryMultipleResult)
        : base(queryMultipleResult.ToDictionary(k => k.Id, v => (IResultSet<TEntity>)new ResultSet<TEntity>(v.Targets, v.TotalCount)))
    {
    }
}