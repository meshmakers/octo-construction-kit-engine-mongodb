using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface ITenantRepository
{
    public string TenantId { get; }
    
    CkTypeGraph GetEntityCacheItem(CkId<CkTypeId> ckTypeId);
    
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

    Task<IResultSet<CkType>> GetCkEntityAsync(IOctoSession session, IReadOnlyList<CkTypeId> ckTypeIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<RtEntity?> GetRtEntityByRtIdAsync(IOctoSession session, RtEntityId rtEntityId);

    Task<TEntity?> GetRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new();

    Task<IResultSet<RtEntity>> GetRtEntitiesByIdAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, IReadOnlyList<OctoObjectId> rtIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId, CkId<CkTypeId> targetCkTypeId,
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

    Task<RtAssociation?> GetRtAssociationOrDefaultAsync(IOctoSession session, RtEntityId rtEntityIdOrigin,
        RtEntityId rtEntityIdTarget,
        CkId<CkAssociationRoleId> roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections, CkId<CkAssociationRoleId> roleId);

    Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections);

    Task<IResultSet<RtEntity>> GetRtEntitiesByTypeAsync(IOctoSession session, CkId<CkTypeId> ckTypeId,
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

    RtEntity CreateTransientRtEntity(CkId<CkTypeId> ckTypeId);

    // ReSharper disable once UnusedMemberInSuper.Global
    RtEntity CreateTransientRtEntity(CkTypeGraph ckTypeGraph);
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

    IUpdateStream<RtEntity> SubscribeToRtEntities(CkId<CkTypeId> ckTypeId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);

    IUpdateStream<TEntity> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new();

    IUpdateStream<RtAssociation> SubscribeToRtAssociations(CkId<CkTypeId> originCkTypeId, CkId<CkTypeId> targetCkTypeId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);

    IUpdateStream<RtAssociation> SubscribeToRtAssociations<TOriginEntity, TTargetEntity>(UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TOriginEntity : RtEntity, new() where TTargetEntity : RtEntity, new();

    Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session, CkId<CkTypeId> ckTypeId,
        string attributeName,
        string regexFilterValue, int takeCount);

    Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckTypeId, string attributeName,
        IEnumerable<object> autoCompleteValues);

    #endregion Advanced functionality
}