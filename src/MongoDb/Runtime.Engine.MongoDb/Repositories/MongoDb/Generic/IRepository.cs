using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Basic interface with a few methods for adding, deleting, and querying data.
/// </summary>
public interface IRepository
{
    Task CreateCollectionIfNotExistsAsync<TCollection>(bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TCollection : class, new();

    IMongoDbDataSourceCollection<TKey, T> GetCollection<TKey, T>(IMongoDataSourceMapper<TKey, T> mongoDataSourceMapper,
        string? suffix = null)
        where T : class, new() where TKey : notnull;

    Task<ObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(ObjectId largeBinaryId, string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadInfo> GetLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);
}