using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Mutation;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class TenantRepository : ITenantRepositoryInternal
{
    private readonly string _tenantId;
    private readonly ICkCacheService _ckCache;
    private readonly IDatabaseContext _databaseContext;

    public TenantRepository(string tenantId, ICkCacheService ckCache, IDatabaseContext databaseContext)
    {
        _tenantId = tenantId;
        _ckCache = ckCache;
        _databaseContext = databaseContext;
    }


    #region Helper

    public string TenantId => _tenantId;

    public CkTypeGraph GetEntityCacheItem(CkId<CkTypeId> ckTypeId)
    {
        var entityCacheItem = _ckCache.GetCkType(_tenantId, ckTypeId);
        if (entityCacheItem == null)
        {
            throw new OperationFailedException($"Type '{ckTypeId}' does not exist.");
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
        foreach (var groupedEntities in rtEntityList.GroupBy(x => x.CkTypeId))
        {
            if (string.IsNullOrWhiteSpace(groupedEntities.Key.FullName))
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkTypeId. Please provide a CkTypeId.");
            }

            results.Add(await _databaseContext.GetRtCollection<RtEntity>(groupedEntities.Key)
                .BulkImportAsync(session, groupedEntities));
        }

        return new AggregatedBulkImportResult(results);
    }
    
    public RtAssociation CreateTransientRtAssociation(RtEntityId originRtEntityId, CkId<CkAssociationRoleId> roleId, RtEntityId targetRtEntityId)
    {
        return new RtAssociation
        {
            AssociationRoleId = roleId,
            OriginCkTypeId = originRtEntityId.CkTypeId,
            OriginRtId = originRtEntityId.RtId,
            TargetCkTypeId = targetRtEntityId.CkTypeId,
            TargetRtId = targetRtEntityId.RtId
        };
    }

    public async Task InsertOneRtEntityAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, RtEntity rtEntity)
    {
        var rtCollection = _databaseContext.GetRtCollection<RtEntity>(ckTypeId);
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
        if (string.IsNullOrWhiteSpace(rtEntity.CkTypeId.FullName)) {
            rtEntity.CkTypeId = rtEntity.GetCkTypeId();
        }
    }

    public async Task ReplaceOneRtEntityByIdAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, OctoObjectId rtId, RtEntity rtEntity)
    {
        var rtCollection = _databaseContext.GetRtCollection<RtEntity>(ckTypeId);
        await rtCollection.ReplaceByIdAsync(session, rtId, rtEntity);
    }

    public async Task ReplaceOneRtEntityByIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId, TEntity rtEntity) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>();
        await rtCollection.ReplaceByIdAsync(session, rtId, rtEntity);
    }

    public async Task DeleteOneRtEntityByRtIdAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, OctoObjectId rtId)
    {
        var rtCollection = _databaseContext.GetRtCollection<RtEntity>(ckTypeId);
        await rtCollection.DeleteOneAsync(session, rtId);
    }

    public async Task DeleteOneRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>();
        await rtCollection.DeleteOneAsync(session, rtId);
    }

    public async Task DeleteOneRtEntityAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, ICollection<FieldFilter> fieldFilters)
    {
        await DeleteOneRtEntityAsync<RtEntity>(session, ckTypeId, fieldFilters);
    }

    public async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, ICollection<FieldFilter> fieldFilters) where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();
        
        await DeleteOneRtEntityAsync<TEntity>(session, ckTypeId, fieldFilters);
    }
    
    private async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId, ICollection<FieldFilter> fieldFilters) where TEntity : RtEntity, new()
    {
        var rtCollection = _databaseContext.GetRtCollection<TEntity>(ckTypeId);

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

    public async Task<IResultSet<CkEntity>> GetCkEntityAsync(IOctoSession session, IReadOnlyList<CkTypeId> ckTypeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var resultSet = new List<CkEntity>();
        long totalCount = 0;

        var query = new CkEntityQuery(_databaseContext);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(ckTypeIds);
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
        return await _databaseContext.GetRtCollection<RtEntity>(rtEntityId.CkTypeId)
            .DocumentAsync(session, rtEntityId.RtId.ToObjectId());
    }

    public async Task<TEntity?> GetRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();

        return await _databaseContext.GetRtCollection<TEntity>(ckTypeId)
            .DocumentAsync(session, rtId.ToObjectId());
    }

    public async Task<IResultSet<RtEntity>> GetRtEntitiesByIdAsync(IOctoSession session, CkId<CkTypeId> ckTypeId,
        IReadOnlyList<OctoObjectId> rtIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        if (!rtIds.Any())
        {
            return new ResultSet<RtEntity>(new List<RtEntity>(), 0);
        }

        var resultSet = new List<RtEntity>();
        long totalCount = 0;
        var entityCacheItem = GetEntityCacheItem(ckTypeId);

        var query =
            new SingleOriginRtQuery(_ckCache, _tenantId, entityCacheItem, _databaseContext, dataQueryOperation.Language);
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
        RtEntityId rtEntityId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirections)
    {
        long counter = 0;
        if (graphDirections == GraphDirections.Inbound || graphDirections == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, rtEntityId.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, roleId)
            );

            var r = await _databaseContext.RtAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (graphDirections == GraphDirections.Outbound || graphDirections == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, rtEntityId.CkTypeId),
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
        CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var result = await GetRtAssociationTargetsAsync(session, new[] { originRtId }, originCkTypeId, roleId, targetCkTypeId,
            graphDirection, rtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkAssociationRoleId> roleId,
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
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId, CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var entityCacheItem = GetEntityCacheItem(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginHierarchicalRtQuery(_ckCache, _tenantId, entityCacheItem, _databaseContext, dataQueryOperation.Language,
                originRtIds,
                originCkTypeId, roleId, graphDirection, targetCkTypeId);

        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        var entityCacheItem = GetEntityCacheItem(targetCkTypeId);

        var originHierarchicalRtQuery =
            new MultipleOriginHierarchicalRtQuery<TTargetEntity>(_ckCache, _tenantId, entityCacheItem, _databaseContext,
                dataQueryOperation.Language,
                originRtIds,
                originCkTypeId, roleId, graphDirection, targetCkTypeId);

        originHierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        originHierarchicalRtQuery.AddIdFilter(rtIds);
        originHierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        originHierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        originHierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await originHierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<TTargetEntity>?> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session, OctoObjectId originRtId,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var dataQueryOperation = new DataQueryOperation();
        
        var resultSets = await GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, new []{originRtId}, roleId, graphDirection, null, dataQueryOperation);
        return resultSets[originRtId];
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session, IEnumerable<OctoObjectId> originRtIds,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {

        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        var entityCacheItem = GetEntityCacheItem(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectHierarchicalRtQuery<TTargetEntity>(_ckCache, _tenantId, entityCacheItem, _databaseContext,
                dataQueryOperation.Language,
                originRtIds,
                originCkTypeId, roleId, graphDirection, targetCkTypeId);
        
        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<RtEntity>> GetRtEntitiesByTypeAsync(IOctoSession session, CkId<CkTypeId> ckTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        return await GetRtEntitiesByTypeAsync<RtEntity>(session, ckTypeId, dataQueryOperation, skip, take);
    }

    public async Task<IResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null) where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();
        return await GetRtEntitiesByTypeAsync<TEntity>(session, ckTypeId, dataQueryOperation, skip, take);
    }

    private async Task<ResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null) where TEntity : RtEntity, new()
    {
        var entityCacheItem = GetEntityCacheItem(ckTypeId);
        var query =
            new SingleOriginRtQuery<TEntity>(_ckCache, _tenantId, entityCacheItem, _databaseContext, dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<RtAssociation> GetRtAssociationAsync(IOctoSession session, RtEntityId rtEntityIdOrigin,
        RtEntityId rtEntityIdTarget,
        CkId<CkAssociationRoleId> roleId)
    {
        return await _databaseContext.RtAssociations.FindSingleOrDefaultAsync(session, x =>
            x.OriginRtId == rtEntityIdOrigin.RtId
            && x.OriginCkTypeId == rtEntityIdOrigin.CkTypeId
            && x.TargetRtId == rtEntityIdTarget.RtId
            && x.TargetCkTypeId == rtEntityIdTarget.CkTypeId
            && x.AssociationRoleId == roleId);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections, CkId<CkAssociationRoleId> roleId)
    {
        return await GetRtAssociationsAsync(session, OctoObjectId.Parse(rtId), graphDirections, roleId);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, OctoObjectId rtId,
        GraphDirections graphDirections, CkId<CkAssociationRoleId> roleId)
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

    public RtEntity CreateTransientRtEntity(CkId<CkTypeId> ckTypeId)
    {
        var entityCacheItem = _ckCache.GetCkType(_tenantId, ckTypeId);
        return CreateTransientRtEntity<RtEntity>(entityCacheItem);
    }

    public RtEntity CreateTransientRtEntity(CkTypeGraph ckTypeGraph)
    {
        return CreateTransientRtEntity<RtEntity>(ckTypeGraph);
    }

    public TEntity CreateTransientRtEntity<TEntity>() where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();
        if (string.IsNullOrWhiteSpace(ckTypeId.FullName))
        {
            throw new InvalidCkTypeIdException($"No Construction Kit Id for type '{typeof(TEntity).FullName}'" +
                                           $" is defined. Is attribute '{typeof(CkIdAttribute).FullName}' missing?");
        }

        var entityCacheItem = _ckCache.GetCkType(_tenantId, ckTypeId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkTypeIdException($"Construction Kit Id '{ckTypeId}' was not found in model cache." +
                                           " Wrong CkTypeId used?");
        }

        return CreateTransientRtEntity<TEntity>(entityCacheItem);
    }

    private TEntity CreateTransientRtEntity<TEntity>(CkTypeGraph ckTypeGraph)
        where TEntity : RtEntity, new()
    {
        var rtEntity = new TEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = ckTypeGraph.CkTypeId
        };
        foreach (var ckTypeAttributeDto in ckTypeGraph.AllAttributes.Values)
        {
            
            object? value = null;
            if (ckTypeAttributeDto.DefaultValues != null)
            {
                switch (ckTypeAttributeDto.ValueType)
                {
                    case AttributeValueTypesDto.StringArray:
                    case AttributeValueTypesDto.IntArray:
                        value = ckTypeAttributeDto.DefaultValues;
                        break;
                    default:
                        value = ckTypeAttributeDto.DefaultValues.First();
                        break;
                }
            }

            rtEntity.SetAttributeValue(ckTypeAttributeDto.AttributeName, ckTypeAttributeDto.ValueType, value);
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

    public IUpdateStream<RtEntity> SubscribeToRtEntities(CkId<CkTypeId> ckTypeId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        var collection = _databaseContext.GetRtCollection<RtEntity>(ckTypeId);

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
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();

        var collection = _databaseContext.GetRtCollection<TEntity>(ckTypeId);

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

    public IUpdateStream<RtAssociation> SubscribeToRtAssociations(CkId<CkTypeId> originCkTypeId, CkId<CkTypeId> targetCkTypeId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return _databaseContext.RtAssociations.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            var filterList = new List<FilterDefinition<ChangeStreamDocument<RtAssociation>>>
            {
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.OriginCkTypeId).ToCamelCase(), originCkTypeId),
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.TargetCkTypeId).ToCamelCase(), targetCkTypeId)
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
                    "fullDocumentBeforeChange." + nameof(RtAssociation.OriginCkTypeId).ToCamelCase(), originCkTypeId),
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.TargetCkTypeId).ToCamelCase(), targetCkTypeId)
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
        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        return SubscribeToRtAssociations(originCkTypeId, targetCkTypeId, updateStreamFilter, cancellationToken);
    }

    public async Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session,
        CkId<CkTypeId> ckTypeId,
        string attributeName, string regexFilterValue, int takeCount)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);
        ArgumentValidation.ValidateString(nameof(regexFilterValue), regexFilterValue);

        var entityCacheItem = GetEntityCacheItem(ckTypeId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkTypeIdException($"Construction Kit Id '{ckTypeId}' is invalid.");
        }

        if (entityCacheItem.AllAttributes.All(x => x.Value.AttributeName != attributeName))
        {
            throw new InvalidAttributeException(
                $"Attribute '{attributeName}' does not exist at type '{ckTypeId}'");
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

        var collection = _databaseContext.GetRtCollection<RtEntity>(entityCacheItem.CkTypeId);
        var result = collection.Aggregate(session,
            PipelineDefinition<RtEntity, AutoCompleteText>.Create(match, sortByCount, limit));
        return await result.ToListAsync();
    }


    public async Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckTypeId, string attributeName,
        IEnumerable<object> autoCompleteValues)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        var ckEntity =
            await _databaseContext.CkEntities.FindSingleOrDefaultAsync(session, x => x.CkTypeId == ckTypeId);
        if (ckEntity == null)
        {
            throw new EntityNotFoundException($"'{ckTypeId}' does not exist in database.");
        }

        var attribute = ckEntity.Attributes.FirstOrDefault(x => x.AttributeName == attributeName);
        if (attribute == null)
        {
            throw new InvalidAttributeException(
                $"Attribute with name '{attributeName}' does not exist on type '{ckTypeId}'");
        }

        attribute.AutoCompleteValues = autoCompleteValues.ToList();

        try
        {
            await _databaseContext.CkEntities.ReplaceByIdAsync(session, ckEntity.CkTypeId, ckEntity);
        }
        catch (Exception e)
        {
            throw new OperationFailedException("An error occurred during import: " + e.Message, e);
        }
    }

    #endregion Advanced functionality
}