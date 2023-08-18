using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface IDatabaseContext
{
    ICachedCollection<DatabaseEntities.CkModel> CkModels { get; }
    ICachedCollection<CkEntity> CkEntities { get; }
    ICachedCollection<CkAttribute> CkAttributes { get; }
    ICachedCollection<CkAssociationRole> CkAssociationRoles { get; }
    ICachedCollection<CkEntityAssociation> CkEntityAssociations { get; }
    ICachedCollection<CkEntityInheritance> CkEntityInheritances { get; }
    ICachedCollection<RtAssociation> RtAssociations { get; }
    Task<IOctoSession> StartSessionAsync();
    IOctoSession StartSession();

    ICachedCollection<TEntity> GetRtCollection<TEntity>(CkId<CkTypeId> ckTypeId) where TEntity : RtEntity, new();
    ICachedCollection<TEntity> GetRtCollection<TEntity>() where TEntity : RtEntity, new();

    Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session);
    Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, string ckTypeId);
    Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkEntity ckEntity);

    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);

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
