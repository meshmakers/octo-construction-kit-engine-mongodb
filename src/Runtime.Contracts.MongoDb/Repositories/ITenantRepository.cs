using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

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

    Task<IResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session, OctoObjectId originRtId,
        CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null);

    Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new();

    Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TTargetEntity>(
        IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
        where TTargetEntity : RtEntity, new();

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
        IOctoSession session, RtEntityId originRtEntityId, CkId<CkAssociationRoleId> roleId,
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

    /// <summary>
    ///     Gets the rt entity graph that is connected using System/ParentChild association
    /// </summary>
    /// <remarks>
    ///     Associations are included whose origin and target is within the graph
    /// </remarks>
    /// <param name="session">Session object</param>
    /// <param name="originRtIds">Origin runtime entity ids</param>
    /// <param name="originCkTypeId">Origin construction kit type id</param>
    /// <param name="dataQueryOperation">Query operation object that defines further filter options</param>
    /// <param name="skip">Amount of items to skip</param>
    /// <param name="take">Amount of items to take</param>
    /// <returns>Result set object that contains the results based on filter options</returns>
    Task<IResultSet<RtDeepGraphQueryResult>> GetRtDeepGraphAsync(IOctoSession session, IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null);

    #endregion Data query
    
    #region Subscriptions / Watch
    
    /// <summary>
    /// Creates a subscription to the update stream for the given construction kit type.
    /// </summary>
    /// <param name="ckTypeId">Construction kit type identifier</param>
    /// <param name="watchStreamFilter">Filter for the update stream</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>Update stream object that can be used to receive updates</returns>
    Task<IUpdateStream<RtEntity>> WatchRtEntitiesAsync(CkId<CkTypeId> ckTypeId, WatchStreamFilter watchStreamFilter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a subscription to the update stream for the given construction kit type.
    /// </summary>
    /// <param name="watchStreamFilter">Filter for the update stream</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <typeparam name="TEntity">Type of the runtime entity</typeparam>
    /// <returns>Update stream object that can be used to receive updates</returns>
    Task<IUpdateStream<TEntity>> WatchRtEntitiesAsync<TEntity>(WatchStreamFilter watchStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new();

    /// <summary>
    /// Creates a subscription to the update stream for the given construction kit association.
    /// </summary>
    /// <param name="originCkTypeId">Construction kit type identifier of the origin</param>
    /// <param name="targetCkTypeId">Construction kit type identifier of the target</param>
    /// <param name="updateStreamFilter">Filter for the update stream</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>Update stream object that can be used to receive updates</returns>
    IUpdateStream<RtAssociation> WatchToRtAssociationsAsync(CkId<CkTypeId> originCkTypeId, CkId<CkTypeId> targetCkTypeId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a subscription to the update stream for the given construction kit association.
    /// </summary>
    /// <param name="updateStreamFilter">Filter for the update stream</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <typeparam name="TOriginEntity">Type of the origin runtime entity</typeparam>
    /// <typeparam name="TTargetEntity">Type of the target runtime entity</typeparam>
    /// <returns>Update stream object that can be used to receive updates</returns>
    IUpdateStream<RtAssociation> WatchToRtAssociationsAsync<TOriginEntity, TTargetEntity>(
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TOriginEntity : RtEntity, new() where TTargetEntity : RtEntity, new();
    
    #endregion Subscriptions

    #region Advanced functionality

    Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session, CkId<CkTypeId> ckTypeId,
        string attributeName,
        string regexFilterValue, int takeCount);

    Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckTypeId, string attributeName,
        IEnumerable<object> autoCompleteValues);

    #endregion Advanced functionality
}
