using System.Collections.Generic;
using System.Linq;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public class ResultSet<TEntity>
{
    public ResultSet(IEnumerable<TEntity> result, long totalCount)
    {
        Result = result;
        TotalCount = totalCount;
    }

    internal ResultSet(QueryResult<TEntity> queryResult)
    {
        Result = queryResult.Result;
        TotalCount = queryResult.TotalCount.FirstOrDefault()?.Count ?? 0;
    }

    public long TotalCount { get; }

    public IEnumerable<TEntity> Result { get; }
}
