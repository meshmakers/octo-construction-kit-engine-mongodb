using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class MultipleOriginResultSet<TEntity> : Dictionary<OctoObjectId, IResultSet<TEntity>>,
    IMultipleOriginResultSet<TEntity>
{
    public MultipleOriginResultSet(List<QueryMultipleResult<TEntity>> queryMultipleResult)
        : base(queryMultipleResult.ToDictionary(k => k.Id, v => (IResultSet<TEntity>) new ResultSet<TEntity>(v.Targets, v.TotalCount)))
    {
    }
}
