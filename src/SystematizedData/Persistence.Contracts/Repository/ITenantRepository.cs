using System.Linq.Expressions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface ITenantRepository
{
    IEntityCacheItem GetEntityCacheItem(CkId<CkTypeId> ckId);
    
    #region Transaction Handling

    Task<IOctoSession> StartSessionAsync();

    #endregion Transaction Handling

    #region Data manipulation
    


    Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList);

    // ReSharper disable once UnusedMember.Global
    Task ApplyChanges(IOctoSession session, IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList);
    Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList);

    #endregion Data manipulation

    #region Data query

    Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session, IReadOnlyList<string> attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<IResultSet<CkEntity>> GetCkEntityAsync(IOctoSession session, IReadOnlyList<CkTypeId> ckIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<RtEntity?> GetRtEntityByRtIdAsync(IOctoSession session, RtEntityId rtEntityId);

    Task<TEntity?> GetRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new();

    Task<IResultSet<RtEntity>> GetRtEntitiesByIdAsync(IOctoSession session, CkId<CkTypeId> ckId, IReadOnlyList<OctoObjectId> rtIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkId, CkId<CkAssociationRoleId> roleId, CkId<CkTypeId> targetCkId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null);

    Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new();

    Task<IResultSet<TTargetEntity>?> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session, OctoObjectId originRtId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new();

    Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new();

    Task<RtAssociation> GetRtAssociationAsync(IOctoSession session, RtEntityId rtEntityIdOrigin,
        RtEntityId rtEntityIdTarget,
        CkId<CkAssociationRoleId> roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections, CkId<CkAssociationRoleId> roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections);

    Task<IResultSet<RtEntity>> GetRtEntitiesByTypeAsync(IOctoSession session, CkId<CkTypeId> ckId,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<IResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null) where TEntity : RtEntity, new();

    // ReSharper disable once UnusedMember.Global
    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections,
        CkId<CkAssociationRoleId> roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections);

    #endregion Data query

    #region Transient data

    RtEntity CreateTransientRtEntity(CkId<CkTypeId> ckId);

    // ReSharper disable once UnusedMemberInSuper.Global
    RtEntity CreateTransientRtEntity(IEntityCacheItem entityCacheItem);
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

    IUpdateStream<RtEntity> SubscribeToRtEntities(CkId<CkTypeId> ckId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);

    IUpdateStream<TEntity> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new();

    IUpdateStream<RtAssociation> SubscribeToRtAssociations(CkId<CkTypeId> originCkId, CkId<CkTypeId> targetCkId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);

    IUpdateStream<RtAssociation> SubscribeToRtAssociations<TOriginEntity, TTargetEntity>(UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TOriginEntity : RtEntity, new() where TTargetEntity : RtEntity, new();

    Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session, CkId<CkTypeId> ckId,
        string attributeName,
        string regexFilterValue, int takeCount);

    Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckId, string attributeName,
        IEnumerable<string> autoCompleteTexts);

    #endregion Advanced functionality
}