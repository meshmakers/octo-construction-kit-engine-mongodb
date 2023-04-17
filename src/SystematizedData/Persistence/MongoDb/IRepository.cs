using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.DataAccess;
using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence.MongoDb;

/// <summary>
///     Basic interface with a few methods for adding, deleting, and querying data.
/// </summary>
public interface IRepository
{
    Task<IOctoSession> StartSessionAsync();

    IOctoSession StartSession();


    Task CreateCollectionIfNotExistsAsync<TCollection>(string? suffix = null) where TCollection : class, new();


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
