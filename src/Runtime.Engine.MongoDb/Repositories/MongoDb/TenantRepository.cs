using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class TenantRepository : RuntimeRepositoryBase, ITenantRepository
{
    private readonly IMetricsContext _metricsContext;
    private readonly IModelLoaderService _modelLoaderService;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;

    public TenantRepository(string tenantId, IMetricsContext metricsContext, ICkCacheService ckCache, IModelLoaderService modelLoaderService,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, IBulkRtMutation bulkRtMutation)
        : base(tenantId, ckCache, mongoDbRepositoryDataSource, bulkRtMutation)
    {
        _metricsContext = metricsContext;
        _modelLoaderService = modelLoaderService;
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
    }


    #region Helper

    public async Task<CkTypeGraph> GetEntityCacheItemAsync(CkId<CkTypeId> ckTypeId)
    {
        var ckCacheService = await GetCkCacheServiceAsync();

        var entityCacheItem = ckCacheService.GetCkType(TenantId, ckTypeId);
        if (entityCacheItem == null) throw new OperationFailedException($"Type '{ckTypeId}' does not exist.");

        return entityCacheItem;
    }

    #endregion Helper


    protected override async Task<IResultSet<TEntity>> GetRtEntitiesByIdAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        IReadOnlyList<OctoObjectId> rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null)
    {
        if (!rtIds.Any()) return new ResultSet<TEntity>(new List<TEntity>(), 0, null);

        var ckCacheService = await GetCkCacheServiceAsync();
        var entityCacheItem = await GetEntityCacheItemAsync(ckTypeId);

        var query =
            new SingleOriginRtQuery<TEntity>(_metricsContext, ckCacheService, TenantId, entityCacheItem, _mongoDbRepositoryDataSource,
                dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(rtIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    #region Transaction Handling

    protected override async Task RefreshCkCacheServiceAsync(ICkCacheService ckCacheService)
    {
        var session = await _mongoDbRepositoryDataSource.GetSessionAsync();
        session.StartTransaction();

        await _modelLoaderService.LoadAsync(TenantId, session, _mongoDbRepositoryDataSource);

        await session.CommitTransactionAsync();
    }

    public override async Task<IOctoSession> GetSessionAsync()
    {
        return await _mongoDbRepositoryDataSource.GetSessionAsync();
    }

    #endregion Transaction Handling

    #region Data manipulation

    public async Task<AggregatedBulkImportResult> BulkInsertRtEntitiesAsync(IOctoSession session,
        IEnumerable<RtEntity> rtEntityList)
    {
        var results = new List<IBulkImportResult>();
        foreach (var groupedEntities in rtEntityList.GroupBy(x => x.CkTypeId))
        {
            if (string.IsNullOrWhiteSpace(groupedEntities.Key.FullName))
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkTypeId. Please provide a CkTypeId.");

            results.Add(await _mongoDbRepositoryDataSource.GetRtCollection<RtEntity>(groupedEntities.Key)
                .BulkImportAsync(session, groupedEntities));
        }

        return new AggregatedBulkImportResult(results);
    }

    protected override async Task DeleteManyRtEntitiesAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters)
    {
        var mutation = new Mutation<TEntity>(BulkRtMutation, _mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.DeleteManyAsync(session, ckTypeId).ConfigureAwait(false);
    }

    protected override async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters)
    {
        var mutation = new Mutation<TEntity>(BulkRtMutation, _mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.DeleteOneAsync(session, ckTypeId).ConfigureAwait(false);
    }

    protected override async Task UpdateOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters, TEntity rtEntity)
    {
        var mutation = new Mutation<TEntity>(BulkRtMutation, _mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.UpdateOneAsync(session, ckTypeId, rtEntity).ConfigureAwait(false);
    }

    protected override async Task UpdateManyRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters, TEntity rtEntity)
    {
        var mutation = new Mutation<TEntity>(BulkRtMutation, _mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.UpdateManyAsync(session, ckTypeId, rtEntity).ConfigureAwait(false);
    }

    protected override async Task ReplaceOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters, TEntity rtEntity)
    {
        var mutation = new Mutation<TEntity>(BulkRtMutation, _mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.ReplaceOneAsync(session, ckTypeId, rtEntity).ConfigureAwait(false);
    }

    public async Task<IBulkImportResult> BulkRtAssociationsAsync(IOctoSession session,
        IEnumerable<RtAssociation> rtAssociations)
    {
        return await _mongoDbRepositoryDataSource.RtAssociations.BulkImportAsync(session, rtAssociations);
    }

    #endregion Data manipulation

    #region Data query

    public async Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<CkId<CkAttributeId>> attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var query = new CkAttributeQuery(_metricsContext, _mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(attributeIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkId<CkTypeId>> ckTypeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var query = new CkTypeQuery(_metricsContext, _mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(ckTypeIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
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
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var entityCacheItem = await GetEntityCacheItemAsync(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginHierarchicalRtQuery(ckCacheService, TenantId, entityCacheItem, _mongoDbRepositoryDataSource,
                dataQueryOperation.Language,
                originRtIds,
                originCkTypeId, roleId, graphDirection, targetCkTypeId);

        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        hierarchicalRtQuery.AddGrouping(dataQueryOperation.FieldGroupBy);

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

        var ckCacheService = await GetCkCacheServiceAsync();
        var entityCacheItem = await GetEntityCacheItemAsync(targetCkTypeId);

        var originHierarchicalRtQuery =
            new MultipleOriginHierarchicalRtQuery<TTargetEntity>(ckCacheService, TenantId, entityCacheItem, _mongoDbRepositoryDataSource,
                dataQueryOperation.Language,
                originRtIds,
                originCkTypeId, roleId, graphDirection, targetCkTypeId);

        originHierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        originHierarchicalRtQuery.AddIdFilter(rtIds);
        originHierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        originHierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        originHierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        originHierarchicalRtQuery.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await originHierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<TTargetEntity>?> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var dataQueryOperation = DataQueryOperation.Create();

        var resultSets = await GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, new[] { originRtId }, roleId,
            graphDirection, null, dataQueryOperation);
        return resultSets[originRtId];
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session, IEnumerable<OctoObjectId> originRtIds,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        var ckCacheService = await GetCkCacheServiceAsync();
        var entityCacheItem = await GetEntityCacheItemAsync(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectHierarchicalRtQuery<TTargetEntity>(ckCacheService, TenantId, entityCacheItem,
                _mongoDbRepositoryDataSource,
                dataQueryOperation.Language,
                originRtIds,
                originCkTypeId, roleId, graphDirection, targetCkTypeId);

        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        hierarchicalRtQuery.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    protected override async Task<IResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var entityCacheItem = await GetEntityCacheItemAsync(ckTypeId);
        var query =
            new SingleOriginRtQuery<TEntity>(_metricsContext, ckCacheService, TenantId, entityCacheItem, _mongoDbRepositoryDataSource,
                dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    #endregion Data query


    #region Large binaries

    public async Task<OctoObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(filename), filename);
        ArgumentValidation.ValidateString(nameof(contentType), contentType);

        var objectId = await _mongoDbRepositoryDataSource.UploadLargeBinaryAsync(filename, contentType, stream, cancellationToken);
        return objectId.ToOctoObjectId();
    }

    public async Task ReplaceLargeBinaryAsync(OctoObjectId largeBinaryId, string filename, string contentType,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(filename), filename);
        ArgumentValidation.ValidateString(nameof(contentType), contentType);

        await _mongoDbRepositoryDataSource.ReplaceLargeBinaryAsync(largeBinaryId.ToObjectId(), filename, contentType, stream,
            cancellationToken);
    }

    public async Task DeleteLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default)
    {
        await _mongoDbRepositoryDataSource.DeleteLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _mongoDbRepositoryDataSource.DownloadLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadInfo> GetLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _mongoDbRepositoryDataSource.GetLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    #endregion Large binaries

    #region Advanced functionality

    public IUpdateStream<RtEntity> SubscribeToRtEntities(CkId<CkTypeId> ckTypeId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        var collection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(ckTypeId);

        return collection.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            if (updateStreamFilter.RtId.HasValue)
                return Builders<ChangeStreamDocument<RtEntity>>.Filter.Eq(
                    "fullDocument." + Constants.IdField,
                    updateStreamFilter.RtId.Value.ToObjectId());

            return default;
        }, () => null, cancellationToken);
    }

    public IUpdateStream<TEntity> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();

        var collection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeId);

        return collection.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            if (updateStreamFilter.RtId.HasValue)
                return Builders<ChangeStreamDocument<TEntity>>.Filter.Eq(
                    "fullDocument." + Constants.IdField,
                    updateStreamFilter.RtId.Value.ToObjectId());

            return default;
        }, () => null, cancellationToken);
    }

    public IUpdateStream<RtAssociation> SubscribeToRtAssociations(CkId<CkTypeId> originCkTypeId, CkId<CkTypeId> targetCkTypeId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.Subscribe(updateStreamFilter.UpdateTypes, () =>
        {
            var filterList = new List<FilterDefinition<ChangeStreamDocument<RtAssociation>>>
            {
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.OriginCkTypeId).ToCamelCase(), originCkTypeId),
                Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.TargetCkTypeId).ToCamelCase(), targetCkTypeId)
            };

            if (!string.IsNullOrWhiteSpace(updateStreamFilter.RoleId))
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(), updateStreamFilter.RoleId));

            if (updateStreamFilter.OriginRtId.HasValue)
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.OriginRtId).ToCamelCase(), updateStreamFilter.OriginRtId));

            if (updateStreamFilter.TargetRtId.HasValue)
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.TargetRtId).ToCamelCase(), updateStreamFilter.TargetRtId));

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
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(), updateStreamFilter.RoleId));

            if (updateStreamFilter.OriginRtId.HasValue)
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.OriginRtId).ToCamelCase(), updateStreamFilter.OriginRtId));

            if (updateStreamFilter.TargetRtId.HasValue)
                filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocumentBeforeChange." + nameof(RtAssociation.TargetRtId).ToCamelCase(), updateStreamFilter.TargetRtId));

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

        var entityCacheItem = await GetEntityCacheItemAsync(ckTypeId);
        if (entityCacheItem == null)
        {
            throw InvalidCkTypeIdException.CkTypeIdNotFound(TenantId, ckTypeId);
        }

        if (entityCacheItem.AllAttributes.All(x => x.Value.AttributeName != attributeName))
            throw new InvalidAttributeException(
                $"Attribute '{attributeName}' does not exist at type '{ckTypeId}'");

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

        var collection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(entityCacheItem.CkTypeId);
        var result = collection.Aggregate(session,
            PipelineDefinition<RtEntity, AutoCompleteText>.Create(match, sortByCount, limit));
        return await result.ToListAsync();
    }


    public async Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckTypeId, string attributeName,
        IEnumerable<object> autoCompleteValues)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        var ckEntity =
            await _mongoDbRepositoryDataSource.CkTypes.FindSingleOrDefaultAsync(session, x => x.CkTypeId == ckTypeId);
        if (ckEntity == null) throw new EntityNotFoundException($"'{ckTypeId}' does not exist in database.");

        var attribute = ckEntity.Attributes.FirstOrDefault(x => x.AttributeName == attributeName);
        if (attribute == null)
            throw new InvalidAttributeException(
                $"Attribute with name '{attributeName}' does not exist on type '{ckTypeId}'");

        attribute.AutoCompleteValues = autoCompleteValues.ToList();

        try
        {
            await _mongoDbRepositoryDataSource.CkTypes.ReplaceByIdAsync(session, ckEntity.CkTypeId, ckEntity);
        }
        catch (Exception e)
        {
            throw new OperationFailedException("An error occurred during import: " + e.Message, e);
        }
    }

    #endregion Advanced functionality
}