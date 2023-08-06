using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Mutation;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using MongoDB.Driver;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class TenantRepository : ITenantRepositoryInternal
{
    private readonly ICkCache _ckCache;
    private readonly IDatabaseContext _databaseContext;

    public TenantRepository(ICkCache ckCache, IDatabaseContext databaseContext)
    {
        _ckCache = ckCache;
        _databaseContext = databaseContext;
    }


    #region Helper

    public IEntityCacheItem GetEntityCacheItem(CkId<CkTypeId> ckId)
    {
        var entityCacheItem = _ckCache.GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new OperationFailedException($"Type '{ckId}' does not exist.");
        }

        return entityCacheItem;
    }

    #endregion Helper

    #region Transaction Handling

    public async Task<IOctoSession> StartSessionAsync()
    {
        return await _databaseContext.StartSessionAsync();
    }

    #endregion Transaction Handling

    #region Data manipulation

    public async Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        BulkRtMutation bulkRtMutation =
            new BulkRtMutation(_databaseContext, _ckCache, this, new AutoIncrementModifier(_databaseContext, _ckCache, this));
        await bulkRtMutation.ApplyChanges(session, entityUpdateInfoList, associationUpdateInfoList);
    }

    public async Task ApplyChanges(IOctoSession session,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        await ApplyChanges(session, new List<EntityUpdateInfo>(), associationUpdateInfoList);
    }

    public async Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList)
    {
        await ApplyChanges(session, entityUpdateInfoList, new List<AssociationUpdateInfo>());
    }

    public async Task<AggregatedBulkImportResult> BulkInsertRtEntitiesAsync(IOctoSession session,
        IEnumerable<RtEntity> rtEntityList)
    {
        var results = new List<BulkImportResult>();
        foreach (var groupedEntities in rtEntityList.GroupBy(x => x.CkId))
        {
            if (string.IsNullOrWhiteSpace(groupedEntities.Key.FullName))
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkId. Please provide a CkId.");
            }

            results.Add(await _databaseContext.GetRtCollection<RtEntity>(groupedEntities.Key)
                .BulkImportAsync(session, groupedEntities));
        }

        return new AggregatedBulkImportResult(results);
    }
    
    public RtAssociation CreateTransientRtAssociation(RtEntityId originRtEntityId, CkId<CkAssociationId> roleId, RtEntityId targetRtEntityId)
    {
        return new RtAssociation
        {
            AssociationRoleId = roleId,
            OriginCkId = originRtEntityId.CkId,
            OriginRtId = originRtEntityId.RtId,
            TargetCkId = targetRtEntityId.CkId,
            TargetRtId = targetRtEntityId.RtId
        };
    }

    public async Task InsertOneRtEntityAsync(IOctoSession session, CkId<CkTypeId> ckId, RtEntity rtEntity)
    {
        var rtCollection = _databaseContext.GetRtCollection<RtEntity>(ckId);
        await rtCollection.InsertAsync(session, rtEntity);
    }

    public async Task InsertOneRtEntityAsync<TEntity>(IOctoSession session, TEntity rtEntity) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>();
        PrepareEntityForModification(rtEntity);
        await rtCollection.InsertAsync(session, rtEntity);
    }

    private void PrepareEntityForModification<TEntity>(TEntity rtEntity) where TEntity : RtEntity, new()
    {
        rtEntity.RtChangedDateTime = DateTime.Now;
        if (!rtEntity.RtCreationDateTime.HasValue)
        {
            rtEntity.RtCreationDateTime = rtEntity.RtChangedDateTime;
        }
        if (string.IsNullOrWhiteSpace(rtEntity.CkId.FullName)) {
            rtEntity.CkId = rtEntity.GetCkId();
        }
    }

    public async Task ReplaceOneRtEntityByIdAsync(IOctoSession session, CkId<CkTypeId> ckId, OctoObjectId rtId, RtEntity rtEntity)
    {
        var rtCollection = _databaseContext.GetRtCollection<RtEntity>(ckId);
        await rtCollection.ReplaceByIdAsync(session, rtId, rtEntity);
    }

    public async Task ReplaceOneRtEntityByIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId, TEntity rtEntity) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>();
        await rtCollection.ReplaceByIdAsync(session, rtId, rtEntity);
    }

    public async Task DeleteOneRtEntityByRtIdAsync(IOctoSession session, CkId<CkTypeId> ckId, OctoObjectId rtId)
    {
        var rtCollection = _databaseContext.GetRtCollection<RtEntity>(ckId);
        await rtCollection.DeleteOneAsync(session, rtId);
    }

    public async Task DeleteOneRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>();
        await rtCollection.DeleteOneAsync(session, rtId);
    }

    public async Task DeleteOneRtEntityAsync(IOctoSession session, CkId<CkTypeId> ckId, ICollection<FieldFilter> fieldFilters)
    {
        await DeleteOneRtEntityAsync<RtEntity>(session, ckId, fieldFilters);
    }

    public async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, ICollection<FieldFilter> fieldFilters) where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();
        
        await DeleteOneRtEntityAsync<TEntity>(session, ckId, fieldFilters);
    }
    
    private async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckId, ICollection<FieldFilter> fieldFilters) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>(ckId);

        var mutation = new RtMutation<TEntity>(rtCollection);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.ExecuteDeleteOneAsync(session);
    }

    public async Task<IBulkImportResult> BulkRtAssociationsAsync(IOctoSession session,
        IEnumerable<RtAssociation> rtAssociations)
    {
        return await _databaseContext.RtAssociations.BulkImportAsync(session, rtAssociations);
    }

    #endregion Data manipulation

    #region Data query

    public async Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<string> attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var resultSet = new List<CkAttribute>();
        long totalCount = 0;

        var query = new CkAttributeQuery(_databaseContext);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(attributeIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        var tempResultSet = await query.ExecuteQuery(session, skip, take);
        resultSet.AddRange(tempResultSet.Items);
        totalCount += tempResultSet.TotalCount;

        return new ResultSet<CkAttribute>(resultSet, totalCount);
    }

    public async Task<IResultSet<CkEntity>> GetCkEntityAsync(IOctoSession session, IReadOnlyList<CkTypeId> ckIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var resultSet = new List<CkEntity>();
        long totalCount = 0;

        var query = new CkEntityQuery(_databaseContext);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(ckIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        var tempResultSet = await query.ExecuteQuery(session, skip, take);
        resultSet.AddRange(tempResultSet.Items);
        totalCount += tempResultSet.TotalCount;

        return new ResultSet<CkEntity>(resultSet, totalCount);
    }

    public async Task<RtEntity?> GetRtEntityByRtIdAsync(IOctoSession session, RtEntityId rtEntityId)
    {
        return await _databaseContext.GetRtCollection<RtEntity>(rtEntityId.CkId)
            .DocumentAsync(session, rtEntityId.RtId.ToObjectId());
    }

    public async Task<TEntity?> GetRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();

        return await _databaseContext.GetRtCollection<TEntity>(ckId)
            .DocumentAsync(session, rtId.ToObjectId());
    }

    public async Task<IResultSet<RtEntity>> GetRtEntitiesByIdAsync(IOctoSession session, CkId<CkTypeId> ckId,
        IReadOnlyList<OctoObjectId> rtIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        if (!rtIds.Any())
        {
            return new ResultSet<RtEntity>(new List<RtEntity>(), 0);
        }

        var resultSet = new List<RtEntity>();
        long totalCount = 0;
        var entityCacheItem = GetEntityCacheItem(ckId);

        var query =
            new SingleOriginRtQuery(entityCacheItem, _databaseContext, dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(rtIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        var tempResultSet = await query.ExecuteQuery(session, skip, take);
        resultSet.AddRange(tempResultSet.Items);
        totalCount += tempResultSet.TotalCount;

        return new ResultSet<RtEntity>(resultSet, totalCount);
    }

    public async Task<CurrentMultiplicity> GetCurrentRtAssociationMultiplicityAsync(IOctoSession session,
        RtEntityId rtEntityId, CkId<CkAssociationId> roleId,
        GraphDirections graphDirections)
    {
        long counter = 0;
        if (graphDirections == GraphDirections.Inbound || graphDirections == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkId, rtEntityId.CkId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, roleId)
            );

            var r = await _databaseContext.RtAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (graphDirections == GraphDirections.Outbound || graphDirections == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkId, rtEntityId.CkId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, roleId)
            );

            var r = await _databaseContext.RtAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (counter >= 2)
        {
            return CurrentMultiplicity.Many;
        }

        if (counter == 1)
        {
            return CurrentMultiplicity.One;
        }

        return CurrentMultiplicity.Zero;
    }

    public async Task<IResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session, OctoObjectId originRtId,
        CkId<CkTypeId> originCkId,
        CkId<CkAssociationId> roleId,
        CkId<CkTypeId> targetCkId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var result = await GetRtAssociationTargetsAsync(session, new[] { originRtId }, originCkId, roleId, targetCkId,
            graphDirection, rtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkAssociationId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var result = await GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, new[] { originRtId }, roleId,
            graphDirection, rtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkId, CkId<CkAssociationId> roleId, CkId<CkTypeId> targetCkId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var entityCacheItem = GetEntityCacheItem(targetCkId);

        var hierarchicalRtQuery =
            new MultipleOriginHierarchicalRtQuery(entityCacheItem, _databaseContext, dataQueryOperation.Language,
                originRtIds,
                originCkId, roleId, graphDirection, targetCkId);

        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkAssociationId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var originCkId = RtEntityExtensions.GetCkId<TOriginEntity>();
        var targetCkId = RtEntityExtensions.GetCkId<TTargetEntity>();

        var entityCacheItem = GetEntityCacheItem(targetCkId);

        var originHierarchicalRtQuery =
            new MultipleOriginHierarchicalRtQuery<TTargetEntity>(entityCacheItem, _databaseContext,
                dataQueryOperation.Language,
                originRtIds,
                originCkId, roleId, graphDirection, targetCkId);

        originHierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        originHierarchicalRtQuery.AddIdFilter(rtIds);
        originHierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        originHierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        originHierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await originHierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<TTargetEntity>?> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session, OctoObjectId originRtId,
        CkId<CkAssociationId> roleId,
        GraphDirections graphDirection) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var dataQueryOperation = new DataQueryOperation();
        
        var resultSets = await GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, new []{originRtId}, roleId, graphDirection, null, dataQueryOperation);
        return resultSets[originRtId];
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session, IEnumerable<OctoObjectId> originRtIds,
        CkId<CkAssociationId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {

        var originCkId = RtEntityExtensions.GetCkId<TOriginEntity>();
        var targetCkId = RtEntityExtensions.GetCkId<TTargetEntity>();

        var entityCacheItem = GetEntityCacheItem(targetCkId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectHierarchicalRtQuery<TTargetEntity>(entityCacheItem, _databaseContext,
                dataQueryOperation.Language,
                originRtIds,
                originCkId, roleId, graphDirection, targetCkId);
        
        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<RtEntity>> GetRtEntitiesByTypeAsync(IOctoSession session, CkId<CkTypeId> ckId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        return await GetRtEntitiesByTypeAsync<RtEntity>(session, ckId, dataQueryOperation, skip, take);
    }

    public async Task<IResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null) where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();
        return await GetRtEntitiesByTypeAsync<TEntity>(session, ckId, dataQueryOperation, skip, take);
    }

    private async Task<ResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null) where TEntity : RtEntity, new()
    {
        var entityCacheItem = GetEntityCacheItem(ckId);
        var query =
            new SingleOriginRtQuery<TEntity>(entityCacheItem, _databaseContext, dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<RtAssociation> GetRtAssociationAsync(IOctoSession session, RtEntityId rtEntityIdOrigin,
        RtEntityId rtEntityIdTarget,
        CkId<CkAssociationId> roleId)
    {
        return await _databaseContext.RtAssociations.FindSingleOrDefaultAsync(session, x =>
            x.OriginRtId == rtEntityIdOrigin.RtId
            && x.OriginCkId == rtEntityIdOrigin.CkId
            && x.TargetRtId == rtEntityIdTarget.RtId
            && x.TargetCkId == rtEntityIdTarget.CkId
            && x.AssociationRoleId == roleId);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections, CkId<CkAssociationId> roleId)
    {
        return await GetRtAssociationsAsync(session, OctoObjectId.Parse(rtId), graphDirections, roleId);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections, CkId<CkAssociationId> roleId)
    {
        var associations = new List<RtAssociation>();

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Inbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.TargetRtId == rtId && x.AssociationRoleId == roleId));
        }

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Outbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.OriginRtId == rtId && x.AssociationRoleId == roleId));
        }

        return associations;
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections)
    {
        return await GetRtAssociationsAsync(session, OctoObjectId.Parse(rtId), graphDirections);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections)
    {
        var associations = new List<RtAssociation>();

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Inbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.TargetRtId == rtId));
        }

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Outbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.OriginRtId == rtId));
        }

        return associations;
    }

    #endregion Data query

    #region Transient data

    public RtEntity CreateTransientRtEntity(CkId<CkTypeId> ckId)
    {
        var entityCacheItem = _ckCache.GetEntityCacheItem(ckId);
        return CreateTransientRtEntity<RtEntity>(entityCacheItem);
    }

    public RtEntity CreateTransientRtEntity(IEntityCacheItem entityCacheItem)
    {
        return CreateTransientRtEntity<RtEntity>(entityCacheItem);
    }

    public TEntity CreateTransientRtEntity<TEntity>() where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();
        if (string.IsNullOrWhiteSpace(ckId.FullName))
        {
            throw new InvalidCkIdException($"No Construction Kit Id for type '{typeof(TEntity).FullName}'" +
                                           $" is defined. Is attribute '{typeof(CkIdAttribute).FullName}' missing?");
        }

        var entityCacheItem = _ckCache.GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkIdException($"Construction Kit Id '{ckId}' was not found in model cache." +
                                           " Wrong CkId used?");
        }

        return CreateTransientRtEntity<TEntity>(entityCacheItem);
    }

    private TEntity CreateTransientRtEntity<TEntity>(IEntityCacheItem entityCacheItem)
        where TEntity : RtEntity, new()
    {
        var rtEntity = new TEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkId = entityCacheItem.CkId
        };
        foreach (var attributeCacheItem in entityCacheItem.Attributes.Values)
        {
            var value = attributeCacheItem.DefaultValue;
            rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.AttributeValueType, value);
        }

        return rtEntity;
    }

    #endregion Transient data

    #region Large binaries

    public async Task<OctoObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(filename), filename);
        ArgumentValidation.ValidateString(nameof(contentType), contentType);

        var objectId = await _databaseContext.UploadLargeBinaryAsync(filename, contentType, stream, cancellationToken);
        return objectId.ToOctoObjectId();
    }

    public async Task ReplaceLargeBinaryAsync(OctoObjectId largeBinaryId, string filename, string contentType,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(filename), filename);
        ArgumentValidation.ValidateString(nameof(contentType), contentType);

        await _databaseContext.ReplaceLargeBinaryAsync(largeBinaryId.ToObjectId(), filename, contentType, stream,
            cancellationToken);
    }

    public async Task DeleteLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default)
    {
        await _databaseContext.DeleteLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _databaseContext.DownloadLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadInfo> GetLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _databaseContext.GetLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    #endregion Large binaries

    #region Advanced functionality

    public IUpdateStream<RtEntity> SubscribeToRtEntities(CkId<CkTypeId> ckId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        var collection = _databaseContext.GetRtCollection<RtEntity>(ckId);

        return collection.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            if (updateStreamFilter.RtId.HasValue)
            {
                return Builders<ChangeStreamDocument<RtEntity>>.Filter.Eq(
                    "fullDocument." + Constants.IdField,
                    updateStreamFilter.RtId.Value.ToObjectId());
            }

            return default;
        }, () => null, cancellationToken);
    }

    public IUpdateStream<TEntity> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();

        var collection = _databaseContext.GetRtCollection<TEntity>(ckId);

        return collection.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            if (updateStreamFilter.RtId.HasValue)
            {
                return Builders<ChangeStreamDocument<TEntity>>.Filter.Eq(
                    "fullDocument." + Constants.IdField,
                    updateStreamFilter.RtId.Value.ToObjectId());
            }

            return default;
        }, () => null, cancellationToken);
    }

    public IUpdateStream<RtAssociation> SubscribeToRtAssociations(CkId<CkTypeId> originCkId, CkId<CkTypeId> targetCkId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return _databaseContext.RtAssociations.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            var filterList = new List<FilterDefinition<ChangeStreamDocument<RtAssociation>>>
            {
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.OriginCkId).ToCamelCase(), originCkId),
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.TargetCkId).ToCamelCase(), targetCkId)
            };

            if (!string.IsNullOrWhiteSpace(updateStreamFilter.RoleId))
            {
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(), updateStreamFilter.RoleId));
            }

            if (updateStreamFilter.OriginRtId.HasValue)
            {
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.OriginRtId).ToCamelCase(), updateStreamFilter.OriginRtId));
            }

            if (updateStreamFilter.TargetRtId.HasValue)
            {
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.TargetRtId).ToCamelCase(), updateStreamFilter.TargetRtId));
            }

            return Builders<ChangeStreamDocument<RtAssociation>>.Filter.And(filterList);
        }, () =>
        {
            var filterList = new List<FilterDefinition<ChangeStreamDocument<RtAssociation>>>
            {
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.OriginCkId).ToCamelCase(), originCkId),
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.TargetCkId).ToCamelCase(), targetCkId)
            };

            if (!string.IsNullOrWhiteSpace(updateStreamFilter.RoleId))
            {
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(), updateStreamFilter.RoleId));
            }

            if (updateStreamFilter.OriginRtId.HasValue)
            {
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.OriginRtId).ToCamelCase(), updateStreamFilter.OriginRtId));
            }

            if (updateStreamFilter.TargetRtId.HasValue)
            {
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.TargetRtId).ToCamelCase(), updateStreamFilter.TargetRtId));
            }

            return Builders<ChangeStreamDocument<RtAssociation>>.Filter.And(filterList);
        }, cancellationToken);
    }

    public IUpdateStream<RtAssociation> SubscribeToRtAssociations<TOriginEntity, TTargetEntity>(
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default) where TOriginEntity : RtEntity, new() where TTargetEntity : RtEntity, new()
    {
        var originCkId = RtEntityExtensions.GetCkId<TOriginEntity>();
        var targetCkId = RtEntityExtensions.GetCkId<TTargetEntity>();

        return SubscribeToRtAssociations(originCkId, targetCkId, updateStreamFilter, cancellationToken);
    }

    public async Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session,
        CkId<CkTypeId> ckId,
        string attributeName, string regexFilterValue, int takeCount)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);
        ArgumentValidation.ValidateString(nameof(regexFilterValue), regexFilterValue);

        var entityCacheItem = GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkIdException($"Construction Kit Id '{ckId}' is invalid.");
        }

        if (!entityCacheItem.Attributes.Keys.Contains(attributeName))
        {
            throw new InvalidAttributeException(
                $"Attribute '{attributeName}' does not exist at type '{ckId}'");
        }

        var match = new BsonDocument
        {
            {
                "$match", new BsonDocument
                {
                    {
                        $"attributes.{attributeName.ToCamelCase()}", new BsonDocument
                        {
                            {
                                "$regex", regexFilterValue
                            }
                        }
                    }
                }
            }
        };

        var sortByCount = new BsonDocument
        {
            {
                "$sortByCount", $"$attributes.{attributeName.ToCamelCase()}"
            }
        };

        var limit = new BsonDocument
        {
            {
                "$limit", takeCount
            }
        };

        var collection = _databaseContext.GetRtCollection<RtEntity>(entityCacheItem.CkId);
        var result = collection.Aggregate(session,
            PipelineDefinition<RtEntity, AutoCompleteText>.Create(match, sortByCount, limit));
        return await result.ToListAsync();
    }


    public async Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckId, string attributeName,
        IEnumerable<string> autoCompleteTexts)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        var ckEntity =
            await _databaseContext.CkEntities.FindSingleOrDefaultAsync(session, x => x.CkId == ckId);
        if (ckEntity == null)
        {
            throw new EntityNotFoundException($"'{ckId}' does not exist in database.");
        }

        var attribute = ckEntity.Attributes.FirstOrDefault(x => x.AttributeName == attributeName);
        if (attribute == null)
        {
            throw new InvalidAttributeException(
                $"Attribute with name '{attributeName}' does not exist on type '{ckId}'");
        }

        attribute.AutoCompleteTexts = autoCompleteTexts.ToList();

        try
        {
            await _databaseContext.CkEntities.ReplaceByIdAsync(session, ckEntity.CkId, ckEntity);
        }
        catch (Exception e)
        {
            throw new OperationFailedException("An error occurred during import: " + e.Message, e);
        }
    }

    #endregion Advanced functionality
}