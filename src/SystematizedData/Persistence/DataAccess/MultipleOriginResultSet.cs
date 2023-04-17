using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

internal class MultipleOriginResultSet<TEntity> : Dictionary<ObjectId, ResultSet<TEntity>>,
    IMultipleOriginResultSet<TEntity>
{
    public MultipleOriginResultSet(List<QueryMultipleResult<TEntity>> queryMultipleResult)
        : base(queryMultipleResult.ToDictionary(k => k.Id, v => new ResultSet<TEntity>(v.Targets, v.TotalCount)))
    {
    }
}
