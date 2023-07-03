using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable once MemberCanBePrivate.Global
public class QueryResult<TEntity>
{
    public IEnumerable<QueryTotalCount> TotalCount { get; set; }
    public IEnumerable<TEntity> Result { get; set; }
}
