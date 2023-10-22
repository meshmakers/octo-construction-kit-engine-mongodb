using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface IDatabaseContext : ICkDatabaseContext, IRepositoryDataSource
{
    IDatabaseCollection<OctoObjectId, RtAssociation> RtDatabaseAssociations { get; }
    
    /// <summary>
    /// Returns the data source access object for the given entity type
    /// </summary>
    /// <param name="ckTypeId">Construction kit type id</param>
    /// <typeparam name="TEntity">The type of entity derived from &lt;see cref="RtEntity"/&gt;</typeparam>
    /// <returns></returns>
    IDatabaseCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkId<CkTypeId> ckTypeId) where TEntity : RtEntity, new();

    /// <summary>
    /// Returns the data source access object for the given entity type
    /// </summary>
    /// <typeparam name="TEntity">The type of entity derived from &lt;see cref="RtEntity"/&gt;</typeparam>
    /// <returns></returns>
    IDatabaseCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>() where TEntity : RtEntity, new();
    
    Task<IOctoSession> GetSessionAsync();
    IOctoSession StartSession();


    Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session);
    Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, string ckTypeId);
    Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkType ckType);


    #region Large Binaries

    Task<ObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(ObjectId largeBinaryId, string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadInfo> GetLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default);

    #endregion Large Binaries
}
