using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

public interface ITenantRepository : IRuntimeRepository
{

    #region Data query

    Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session, IReadOnlyList<CkId<CkAttributeId>> attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkId<CkTypeId>> ckTypeIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);
    
    Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session, List<CkId<CkRecordId>> ckRecordIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, List<CkId<CkEnumId>> ckEnumIds,
        DataQueryOperation dataQueryOperation, 
        int? skip = null, int? take = null);

    Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
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

    #endregion Data query

    #region Large Binaries

    Task<OctoObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream, Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(OctoObjectId largeBinaryId, string filename, string contentType, Stream stream, Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);
    
    Task<OctoObjectId> ReplaceLargeBinaryAsync(string filename, string contentType, Stream stream, Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    Task DeleteLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default);

    Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default);

    Task<IDownloadInfo?> GetLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default);
    Task<IDownloadInfo?> GetLargeBinaryAsync(string fileName, CancellationToken cancellationToken = default);

    #endregion Large Binaries

    #region Advanced functionality

    Task<AggregatedBulkImportResult>
        BulkInsertRtEntitiesAsync(IOctoSession session, IEnumerable<RtEntity> rtEntityList);

    Task<IBulkImportResult> BulkRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations);

    Task<IUpdateStream<RtEntity>> SubscribeToRtEntities(CkId<CkTypeId> ckTypeId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);

    Task<IUpdateStream<TEntity>> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
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

    /// <summary>
    /// Loads the cache for the tenant.
    /// </summary>
    /// <returns></returns>
    Task LoadCacheForTenantAsync(ICkCacheService cacheService);
}