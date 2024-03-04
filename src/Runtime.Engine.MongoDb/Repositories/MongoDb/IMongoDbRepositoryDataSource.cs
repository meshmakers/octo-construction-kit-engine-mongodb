using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public interface IMongoDbRepositoryDataSource : ICkMongoDbRepositoryDataSource, IRepositoryDataSource
{
    IMongoDbDataSourceCollection<OctoObjectId, RtAssociation> RtMongoDbDataSourceAssociations { get; }

    /// <summary>
    ///     Returns the data source access object for the given entity type
    /// </summary>
    /// <param name="ckTypeGraph">Construction kit type graph</param>
    /// <typeparam name="TEntity">The type of entity derived from &lt;see cref="RtEntity"/&gt;</typeparam>
    /// <returns></returns>
    IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkTypeGraph ckTypeGraph)
        where TEntity : RtEntity, new();

    Task<IOctoSession> GetSessionAsync();
    IOctoSession StartSession();


    Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session);
    Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkId<CkTypeId> ckTypeId);
    Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkType ckType);


    #region Large Binaries

    Task<ObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream, Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(ObjectId largeBinaryId, string filename, string contentType, Stream stream, Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);
    
    Task<ObjectId> ReplaceLargeBinaryAsync(string filename, string contentType, Stream stream, Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadInfo?> GetLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);
    Task<IDownloadInfo?> GetLargeBinaryAsync(string fileName, CancellationToken cancellationToken = default);

    #endregion Large Binaries
}