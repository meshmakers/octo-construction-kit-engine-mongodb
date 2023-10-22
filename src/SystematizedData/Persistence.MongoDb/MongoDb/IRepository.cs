using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

/// <summary>
///     Basic interface with a few methods for adding, deleting, and querying data.
/// </summary>
public interface IRepository
{
    Task CreateCollectionIfNotExistsAsync<TCollection>(bool enableChangeStreamPreAndPostImages, string? suffix = null) where TCollection : class, new();

    IDatabaseCollection<TKey, T> GetCollection<TKey, T>(IMongoDataSourceMapper<TKey, T> mongoDataSourceMapper, string? suffix = null) 
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
