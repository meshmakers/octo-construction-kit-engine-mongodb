using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface IDatabaseContext : ICkDatabaseContext
{
    public IDatabaseCollection<DatabaseEntities.CkModel> CkModelsInternal { get; }
    public IDatabaseCollection<CkAttribute> CkAttributesInternal { get; }
    public IDatabaseCollection<CkType> CkTypesInternal { get; }

    IDatabaseCollection<RtAssociation> RtAssociations { get; }
    Task<IOctoSession> StartSessionAsync();
    IOctoSession StartSession();

    IDatabaseCollection<TEntity> GetRtCollection<TEntity>(CkId<CkTypeId> ckTypeId) where TEntity : RtEntity, new();
    IDatabaseCollection<TEntity> GetRtCollection<TEntity>() where TEntity : RtEntity, new();

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
