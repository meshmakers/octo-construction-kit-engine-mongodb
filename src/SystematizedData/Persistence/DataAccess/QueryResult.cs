using System.Collections.Generic;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable once MemberCanBePrivate.Global
internal class QueryResult<TEntity>
{
    public IEnumerable<QueryTotalCount> TotalCount { get; set; }
    public IEnumerable<TEntity> Result { get; set; }
}
