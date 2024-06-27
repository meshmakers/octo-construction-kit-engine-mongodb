using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

public interface ITenantRepository : IRuntimeRepository
{
    /// <summary>
    /// Gets the session for the tenant.
    /// </summary>
    /// <returns></returns>
    IOctoSession GetSession();

    #region Data query

    /// <summary>
    /// Gets the construction kit models.
    /// </summary>
    /// <param name="session">Octo session</param>
    /// <param name="ckModelIds">List of construction kit model ids, when null all is returned, based on the further filter options</param>
    /// <param name="dataQueryOperation">Data query filter and sorting options</param>
    /// <param name="skip">Skips the defined amount of items, when null no items are skipped</param>
    /// <param name="take">Takes the defined amount of items, when null all items are taken</param>
    /// <returns>Result set object that contains the results based on filter options</returns>
    Task<IResultSet<CkModel>> GetCkModelsAsync(IOctoSession session, List<CkModelId>? ckModelIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    /// <summary>
    /// Gets the construction kit attributes.
    /// </summary>
    /// <param name="session">Octo session</param>
    /// <param name="ckModelIds">List of construction kit model ids, when null all is returned, based on the further filter options</param>
    /// <param name="attributeIds">List of construction kit attribute ids, when null all is returned, based on the further filter options</param>
    /// <param name="dataQueryOperation">Data query filter and sorting options</param>
    /// <param name="skip">Skips the defined amount of items, when null no items are skipped</param>
    /// <param name="take">Takes the defined amount of items, when null all items are taken</param>
    /// <returns>Result set object that contains the results based on filter options</returns>
    Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds,
        IReadOnlyList<CkId<CkAttributeId>>? attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    /// <summary>
    /// Gets the construction kit types.
    /// </summary>
    /// <param name="session">Octo session</param>
    /// <param name="ckModelIds">List of construction kit model ids, when null all is returned, based on the further filter options</param>
    /// <param name="ckTypeIds">List of construction kit type ids, when null all is returned, based on the further filter options</param>
    /// <param name="dataQueryOperation">Data query filter and sorting options</param>
    /// <param name="skip">Skips the defined amount of items, when null no items are skipped</param>
    /// <param name="take">Takes the defined amount of items, when null all items are taken</param>
    /// <returns>Result set object that contains the results based on filter options</returns>
    Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds, IReadOnlyList<CkId<CkTypeId>>? ckTypeIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    /// <summary>
    /// Gets the construction kit records.
    /// </summary>
    /// <param name="session">Octo session</param>
    /// <param name="ckModelIds">List of construction kit model ids, when null all is returned, based on the further filter options</param>
    /// <param name="ckRecordIds">List of construction kit record ids, when null all is returned, based on the further filter options</param>
    /// <param name="dataQueryOperation">Data query filter and sorting options</param>
    /// <param name="skip">Skips the defined amount of items, when null no items are skipped</param>
    /// <param name="take">Takes the defined amount of items, when null all items are taken</param>
    /// <returns>Result set object that contains the results based on filter options</returns>
    Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds, List<CkId<CkRecordId>>? ckRecordIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    /// <summary>
    /// Gets the construction kit enums.
    /// </summary>
    /// <param name="session">Octo session</param>
    /// <param name="ckModelIds">List of construction kit model ids, when null all is returned, based on the further filter options</param>
    /// <param name="ckEnumIds">List of construction kit enum ids, when null all is returned, based on the further filter options</param>
    /// <param name="dataQueryOperation">Data query filter and sorting options</param>
    /// <param name="skip">Skips the defined amount of items, when null no items are skipped</param>
    /// <param name="take">Takes the defined amount of items, when null all items are taken</param>
    /// <returns>Result set object that contains the results based on filter options</returns>
    Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds, List<CkId<CkEnumId>>? ckEnumIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null);

    Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null);

    Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new();

    Task<IResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session, OctoObjectId originRtId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new();

    Task<IResultSet<RtEntity>> GetIndirectRtAssociationTargetsAsync(
        IOctoSession session, OctoObjectId originRtId, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId, GraphDirections graphDirection);

    Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new();

    Task<IMultipleOriginResultSet<RtEntity>> GetIndirectRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, CkId<CkTypeId> targetCkTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null);

    #endregion Data query

    #region Large Binaries

    Task<OctoObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    Task ReplaceLargeBinaryAsync(OctoObjectId largeBinaryId, string filename, string contentType, Stream stream,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken = default);

    Task<OctoObjectId> ReplaceLargeBinaryAsync(string filename, string contentType, Stream stream,
        Dictionary<string, object> metadata,
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

    IUpdateStream<RtAssociation> SubscribeToRtAssociations<TOriginEntity, TTargetEntity>(
        UpdateAssociationStreamFilter updateStreamFilter,
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