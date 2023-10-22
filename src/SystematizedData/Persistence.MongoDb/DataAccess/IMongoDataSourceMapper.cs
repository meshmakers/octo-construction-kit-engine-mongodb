using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

/// <summary>
/// Interface for mapping the id of a document
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TDocument"></typeparam>
public interface IMongoDataSourceMapper<out TKey, TDocument> where TKey : notnull
{
    /// <summary>
    /// Returns the id of an object
    /// </summary>
    /// <param name="document"></param>
    /// <returns></returns>
    TKey GetId(TDocument document);
    
    /// <summary>
    /// Update a document in database based on given document
    /// </summary>
    /// <param name="document"></param>
    UpdateDefinition<TDocument> ApplyUpdate(TDocument document);
}