// ReSharper disable PropertyCanBeMadeInitOnly.Global
namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable once MemberCanBePrivate.Global
public class QueryResult<TEntity>
{
    public IEnumerable<QueryTotalCount> TotalCount { get; set; } = null!;
    public IEnumerable<TEntity> Result { get; set; } = null!;
}
