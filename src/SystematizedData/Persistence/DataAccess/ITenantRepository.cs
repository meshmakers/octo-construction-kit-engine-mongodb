using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface ITenantRepository
{
    #region Transaction Handling

    Task<IOctoSession> StartSessionAsync();

    IOctoSession StartSession();

    #endregion Transaction Handling

    #region Data manipulation

    Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList);

    // ReSharper disable once UnusedMember.Global
    Task ApplyChanges(IOctoSession session, IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList);
    Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList);

    #endregion Data manipulation

    #region Data query

    Task<ResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session, IReadOnlyList<string> attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<ResultSet<CkEntity>> GetCkEntityAsync(IOctoSession session, IReadOnlyList<string> ckIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<RtEntity?> GetRtEntityAsync(IOctoSession session, RtEntityId rtEntityId);

    Task<TEntity?> GetRtEntityAsync<TEntity>(IOctoSession session, RtEntityId rtEntityId)
        where TEntity : RtEntity, new();

    Task<ResultSet<RtEntity>> GetRtEntitiesByIdAsync(IOctoSession session, string ckId, IReadOnlyList<ObjectId> rtIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<ObjectId> originRtIds, string originCkId, string roleId, string targetCkId,
        GraphDirections graphDirection, IReadOnlyList<ObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<ObjectId> originRtIds, string roleId,
        GraphDirections graphDirection, IReadOnlyList<ObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new();

    Task<RtAssociation> GetRtAssociationAsync(IOctoSession session, RtEntityId rtEntityIdOrigin,
        RtEntityId rtEntityIdTarget,
        string roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, ObjectId rtId,
        GraphDirections graphDirections, string roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, ObjectId rtId,
        GraphDirections graphDirections);

    Task<ResultSet<RtEntity>> GetRtEntitiesByTypeAsync(IOctoSession session, string ckId,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<ResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null) where TEntity : RtEntity, new();

    // ReSharper disable once UnusedMember.Global
    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections,
        string roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections);

    #endregion Data query

    #region Transient data

    RtEntity CreateTransientRtEntity(string ckId);

    // ReSharper disable once UnusedMemberInSuper.Global
    RtEntity CreateTransientRtEntity(EntityCacheItem entityCacheItem);
    TEntity CreateTransientRtEntity<TEntity>() where TEntity : RtEntity, new();

    #endregion Transient data

    #region Large Binaries

    Task<OctoObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(OctoObjectId largeBinaryId, string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadInfo> GetLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default);

    #endregion Large Binaries

    #region Advanced functionality

    IUpdateStream<RtEntity> SubscribeToRtEntities(string ckId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);
    
    IUpdateStream<TEntity> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new();

    Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session, string ckId,
        string attributeName,
        string regexFilterValue, int takeCount);

    Task UpdateAutoCompleteTexts(IOctoSession session, string rtId, string attributeName,
        IEnumerable<string> autoCompleteTexts);

    #endregion Advanced functionality
}
