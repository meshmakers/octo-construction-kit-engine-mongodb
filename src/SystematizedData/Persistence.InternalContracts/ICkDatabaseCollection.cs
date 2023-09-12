using System.Linq.Expressions;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface ICkDatabaseCollection<TDocument> where TDocument : class, new()
{
    Task InsertAsync(IOctoSession session, TDocument document);
    
    Task<IBulkImportResult> BulkImportAsync(IOctoSession session, IEnumerable<TDocument> documentList);
    
    Task<ICollection<TDocument>> FindManyAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression,
        int? skip = null, int? limit = null);
    
    Task DeleteOneAsync<TField>(IOctoSession session, TField id);
    
    Task TryDeleteOneAsync<TField>(IOctoSession session, TField id);
    
    Task<TDocument?> FindSingleOrDefaultAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);

    Task ReplaceByIdAsync<TField>(IOctoSession session, TField id, TDocument document);

}