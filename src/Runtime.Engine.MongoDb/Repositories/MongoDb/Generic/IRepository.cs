namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Basic interface with a few methods for adding, deleting, and querying data.
/// </summary>
public interface IRepository
{
    Task CreateCollectionIfNotExistsAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TDocument : class,
        new()
        where TKey : notnull;

    /// <summary>
    ///     Reconciles the <c>changeStreamPreAndPostImages.enabled</c> option on an existing
    ///     collection to match the desired value. The create-time option is ignored by MongoDB
    ///     once the collection exists, so this <c>collMod</c> path is required to flip the flag
    ///     when a CK type's <c>EnableChangeStreamPreAndPostImages</c> changes after first import.
    ///     No-op when the collection is absent, when the current value already matches, or when
    ///     the server is older than MongoDB 6.0.
    /// </summary>
    Task ReconcileChangeStreamPreAndPostImagesAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TDocument : class,
        new()
        where TKey : notnull;

    IMongoDbDataSourceCollection<TKey, TDocument> GetCollection<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TDocument : class,
        new()
        where TKey : notnull;
}
