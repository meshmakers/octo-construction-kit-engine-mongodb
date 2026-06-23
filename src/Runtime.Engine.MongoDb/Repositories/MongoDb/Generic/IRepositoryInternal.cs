using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal interface IRepositoryInternal : IRepository
{
    /// <summary>
    /// Underlying MongoDB driver handle for this tenant's database. Exposed so engine-internal
    /// observability paths (e.g. <see cref="IndexUsageService"/> running <c>$indexStats</c>)
    /// can issue driver-level aggregations without going through the dynamic CK-typed
    /// collection wrappers. Kept on the internal interface so this Mongo-driver type does not
    /// leak into the public engine API.
    /// </summary>
    IMongoDatabase Database { get; }

    string GetCollectionName<TKey, TDocument>(IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new();

    IGridFSBucket GetGridFsBucket();

    /// <summary>
    /// Lists all collection names in the database that match the given prefix.
    /// </summary>
    /// <param name="prefix">The prefix to filter collections by</param>
    /// <returns>List of collection names</returns>
    Task<IReadOnlyList<string>> ListCollectionNamesAsync(string prefix);

    /// <summary>
    /// Drops a collection by name.
    /// </summary>
    /// <param name="collectionName">The name of the collection to drop</param>
    Task DropCollectionAsync(string collectionName);

    /// <summary>
    /// Gets the document count for a collection.
    /// </summary>
    /// <param name="collectionName">The name of the collection</param>
    /// <returns>The number of documents in the collection</returns>
    Task<long> GetCollectionDocumentCountAsync(string collectionName);

    /// <summary>
    /// Checks whether a collection contains any documents. Uses Find with Limit(1) which is
    /// significantly faster than CountDocumentsAsync on large collections.
    /// </summary>
    /// <param name="collectionName">The name of the collection</param>
    /// <returns>True if the collection contains at least one document</returns>
    Task<bool> CollectionHasDocumentsAsync(string collectionName);
}
