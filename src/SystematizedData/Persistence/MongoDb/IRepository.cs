using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

/// <summary>
///     Basic interface with a few methods for adding, deleting, and querying data.
/// </summary>
public interface IRepository
{
    Task<IOctoSession> StartSessionAsync();

    IOctoSession StartSession();


    Task CreateCollectionIfNotExistsAsync<TCollection>(bool enableChangeStreamPreAndPostImages, string? suffix = null) where TCollection : class, new();


    ICachedCollection<T> GetCollection<T>(string? suffix = null) where T : class, new();

    Task<ObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(ObjectId largeBinaryId, string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadInfo> GetLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);
}
