using System.Collections.Generic;
using System.Linq;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class ResultSet<TEntity> : IResultSet<TEntity>
{
    public ResultSet(IEnumerable<TEntity> result, long totalCount)
    {
        Items = result;
        TotalCount = totalCount;
    }

    internal ResultSet(QueryResult<TEntity> queryResult)
    {
        Items = queryResult.Result;
        TotalCount = queryResult.TotalCount.FirstOrDefault()?.Count ?? 0;
    }

    public long TotalCount { get; }

    public IEnumerable<TEntity> Items { get; }
}
