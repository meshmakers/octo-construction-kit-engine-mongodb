using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class MultipleOriginResultSet<TEntity>(List<QueryMultipleResult<TEntity>> queryMultipleResult)
    : Dictionary<RtEntityId, IResultSet<TEntity>>(queryMultipleResult.ToDictionary(k => k.Id,
            v => (IResultSet<TEntity>)new ResultSet<TEntity>(v.Targets, v.TotalCount, v.AggregationResult,
                v.FieldAggregationResults))),
        IMultipleOriginResultSet<TEntity>;
