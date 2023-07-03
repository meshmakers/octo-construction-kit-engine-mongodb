namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IResultSet<out TEntity>
{
    long TotalCount { get; }
    IEnumerable<TEntity> Result { get; }
}