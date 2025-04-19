using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using MongoDB.Bson;

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

    IMongoDbDataSourceCollection<TKey, TDocument> GetCollection<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TDocument : class,
        new()
        where TKey : notnull;

    Task<OctoObjectId> UploadLargeBinaryAsync(IOctoSession session, string filename,
        string contentType, BinaryType binaryType, Stream stream,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        string filename, string contentType, BinaryType binaryType,
        Stream stream, CancellationToken cancellationToken = default);

    Task<OctoObjectId> ReplaceLargeBinaryAsync(IOctoSession session, string filename,
        string contentType, BinaryType binaryType, Stream stream,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(IOctoSession session,
        OctoObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IBinaryInfo?> GetLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IBinaryInfo?> GetLargeBinaryAsync(IOctoSession session, string fileName, BinaryType binaryType,
        CancellationToken cancellationToken = default);
}
