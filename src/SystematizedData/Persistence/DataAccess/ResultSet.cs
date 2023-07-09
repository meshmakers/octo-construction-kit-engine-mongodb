using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class ResultSet<TEntity>
{
    public ResultSet(IEnumerable<TEntity> result, long totalCount, IEnumerable<GroupingDto>? groupingDto)
    {
        Result = result;
        TotalCount = totalCount;
        Grouping = groupingDto;
    }

    internal ResultSet(QueryResult<TEntity> queryResult, IEnumerable<GroupingDto>? groupingDto)
    {
        Result = queryResult.Result;
        TotalCount = queryResult.TotalCount.FirstOrDefault()?.Count ?? 0;
        Grouping = groupingDto;
    }

    public long TotalCount { get; }

    public IEnumerable<TEntity> Result { get; }
    
    public IEnumerable<GroupingDto>? Grouping { get; }
    
}
