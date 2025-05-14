using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
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

internal class TenantRepository(
    string tenantId,
    IMetricsContext metricsContext,
    ICkCacheService ckCache,
    IModelLoaderService modelLoaderService,
    IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
    IBulkRtMutation bulkRtMutation)
    : RuntimeRepositoryBase(tenantId, ckCache, mongoDbRepositoryDataSource, bulkRtMutation), ITenantRepository
{
    #region Transaction Handling

    protected override async Task RefreshCkCacheServiceAsync(ICkCacheService ckCacheService)
    {
        var session = await mongoDbRepositoryDataSource.GetSessionAsync();
        session.StartTransaction();

        await modelLoaderService.LoadAsync(TenantId, session, mongoDbRepositoryDataSource);

        await session.CommitTransactionAsync();
    }

    public override async Task<IOctoSession> GetSessionAsync()
    {
        return await mongoDbRepositoryDataSource.GetSessionAsync();
    }

    public IOctoSession GetSession()
    {
        return mongoDbRepositoryDataSource.GetSession();
    }

    #endregion Transaction Handling

    #region Data manipulation

    public async Task<AggregatedBulkImportResult> BulkInsertRtEntitiesAsync(IOctoSession session,
        IEnumerable<RtEntity> rtEntityList)
    {
        var results = new List<IBulkImportResult>();
        foreach (var groupedEntities in rtEntityList.GroupBy(x => x.CkTypeId))
        {
            if (groupedEntities.Key == null)
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkTypeId. Please provide a CkTypeId.");
            }

            var ckTypeGraph = await GetCkTypeGraphAsync(groupedEntities.Key);

            results.Add(await mongoDbRepositoryDataSource.GetRtCollection<RtEntity>(ckTypeGraph)
                .BulkImportAsync(session, groupedEntities));
        }

        return new AggregatedBulkImportResult(results);
    }

    protected override async Task DeleteManyRtEntitiesAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.DeleteManyAsync(session).ConfigureAwait(false);
    }

    protected override async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.DeleteOneAsync(session).ConfigureAwait(false);
    }

    protected override async Task UpdateOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters, TEntity rtEntity)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.UpdateOneAsync(session, ckTypeId, rtEntity).ConfigureAwait(false);
    }

    protected override async Task UpdateManyRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters, TEntity rtEntity)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.UpdateManyAsync(session, rtEntity).ConfigureAwait(false);
    }

    protected override async Task ReplaceOneRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId,
        ICollection<FieldFilter> fieldFilters, TEntity rtEntity)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource);
        mutation.AddFieldFilters(fieldFilters);
        await mutation.ReplaceOneAsync(session, ckTypeId, rtEntity).ConfigureAwait(false);
    }

    public async Task<IBulkImportResult> BulkRtAssociationsAsync(IOctoSession session,
        IEnumerable<RtAssociation> rtAssociations)
    {
        return await mongoDbRepositoryDataSource.RtAssociations.BulkImportAsync(session, rtAssociations);
    }

    #endregion Data manipulation

    #region Data query

    public async Task<IResultSet<CkModel>> GetCkModelsAsync(IOctoSession session, List<CkModelId>? ckModelIds,
        DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var query = new CkModelQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(ckModelIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<CkModelId>? ckModelIds,
        IReadOnlyList<CkId<CkAttributeId>>? attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var query = new CkAttributeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(attributeIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds,
        IReadOnlyList<CkId<CkTypeId>>? ckTypeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var query = new CkTypeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(ckTypeIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds,
        List<CkId<CkRecordId>>? ckRecordIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var query = new CkRecordQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(ckRecordIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds,
        List<CkId<CkEnumId>>? ckEnumIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var query = new CkEnumQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(ckEnumIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session, OctoObjectId originRtId,
        CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
    {
        var result = await GetRtAssociationTargetsAsync(session, [originRtId], originCkTypeId, roleId,
            targetCkTypeId,
            graphDirection, rtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var result = await GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, [originRtId],
            roleId,
            graphDirection, targetRtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TTargetEntity>(IOctoSession session,
        OctoObjectId originRtId, CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId, GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null) where TTargetEntity : RtEntity, new()
    {
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        var result = await GetRtAssociationTargetsAsync<TTargetEntity>(session, [originRtId], originCkTypeId, roleId, graphDirection,
            targetRtIds, targetCkTypeId, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        CkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
    {
        return GetRtAssociationTargetsAsync<RtEntity>(session, originRtIds, originCkTypeId, roleId, graphDirection,
            targetRtIds, targetCkTypeId, dataQueryOperation, skip, take);
    }

    public Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity,
        TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        return GetRtAssociationTargetsAsync<TTargetEntity>(session, originRtIds, originCkTypeId, roleId, graphDirection,
            targetRtIds, targetCkTypeId, dataQueryOperation, skip, take);
    }

    private async Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<
        TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, CkId<CkTypeId> targetCkTypeId, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null)
        where TTargetEntity : RtEntity, new()
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);
        var targetTypeGraph = await GetCkTypeGraphAsync(targetCkTypeId);

        var originHierarchicalRtQuery =
            new MultipleOriginDirectAssociationsRtQuery<TTargetEntity>(ckCacheService, TenantId,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraph);

        originHierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        originHierarchicalRtQuery.AddIdFilter(targetRtIds);
        originHierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        originHierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        originHierarchicalRtQuery.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        originHierarchicalRtQuery.AddGrouping(dataQueryOperation.FieldGroupBy);
        originHierarchicalRtQuery.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);

        return await originHierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        OctoObjectId originRtId,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var dataQueryOperation = DataQueryOperation.Create();

        var resultSets = await GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session,
            [originRtId], roleId,
            graphDirection, null, dataQueryOperation);
        return resultSets[originRtId];
    }

    public async Task<IResultSet<RtEntity>> GetIndirectRtAssociationTargetsAsync(IOctoSession session,
        OctoObjectId originRtId, CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId, CkId<CkTypeId> targetCkTypeId, GraphDirections graphDirection)
    {
        var dataQueryOperation = DataQueryOperation.Create();

        var resultSets = await GetIndirectRtAssociationTargetsAsync(session, new[] { originRtId }, originCkTypeId,
            roleId,
            graphDirection, null, targetCkTypeId, dataQueryOperation);
        return resultSets[originRtId];
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity,
        TTargetEntity>(
        IOctoSession session, IEnumerable<OctoObjectId> originRtIds,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        var ckCacheService = await GetCkCacheServiceAsync();
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);
        var targetTypeGraph = await GetCkTypeGraphAsync(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectAssociationsRtQuery<TTargetEntity>(ckCacheService, TenantId,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraph);

        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        hierarchicalRtQuery.AddGrouping(dataQueryOperation.FieldGroupBy);
        hierarchicalRtQuery.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IMultipleOriginResultSet<RtEntity>> GetIndirectRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId,
        CkId<CkAssociationRoleId> roleId, GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds,
        CkId<CkTypeId> targetCkTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);
        var targetTypeGraph = await GetCkTypeGraphAsync(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectAssociationsRtQuery<RtEntity>(ckCacheService, TenantId,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraph);

        hierarchicalRtQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtQuery.AddIdFilter(targetRtIds);
        hierarchicalRtQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtQuery.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        hierarchicalRtQuery.AddGrouping(dataQueryOperation.FieldGroupBy);
        hierarchicalRtQuery.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<RtDeepGraphQueryResult>> GetRtDeepGraphAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds,
        CkId<CkTypeId> originCkTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);

        var hierarchicalDeepRtGraphQuery = new MultipleOriginHierarchicalDeepRtGraphQuery(mongoDbRepositoryDataSource,
            dataQueryOperation.Language, originRtIds, originTypeGraph,
            new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild));
        hierarchicalDeepRtGraphQuery.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalDeepRtGraphQuery.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalDeepRtGraphQuery.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalDeepRtGraphQuery.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        hierarchicalDeepRtGraphQuery.AddGrouping(dataQueryOperation.FieldGroupBy);
        hierarchicalDeepRtGraphQuery.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);

        return await hierarchicalDeepRtGraphQuery.ExecuteQuery(session, skip, take);
    }

    protected override async Task<IResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        CkId<CkTypeId> ckTypeId,
        DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var query =
            new SingleOriginRtQuery<TEntity>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);
        query.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);

        return await query.ExecuteQuery(session, skip, take);
    }

    protected override async Task<IResultSet<TEntity>> GetRtEntitiesByIdAsync<TEntity>(IOctoSession session,
        CkId<CkTypeId> ckTypeId,
        IReadOnlyList<OctoObjectId> rtIds, DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null)
    {
        if (!rtIds.Any())
        {
            return new ResultSet<TEntity>(new List<TEntity>(), 0, null);
        }

        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);

        var query =
            new SingleOriginRtQuery<TEntity>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(rtIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);
        query.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);

        return await query.ExecuteQuery(session, skip, take);
    }

    public override async Task<IResultSet<RtEntityGraphItem>> GetRtEntitiesGraphByTypeAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, DataQueryOperation dataQueryOperation,
        IEnumerable<NavigationPair> roleIdDirectionPairs, int? skip = null, int? take = null)
    {

        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var query =
            new SingleOriginRtQuery<RtEntityGraphItem>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);
        query.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);
        query.AddAssociations(roleIdDirectionPairs);

        return await query.ExecuteQuery(session, skip, take);
    }

    public override async Task<IResultSet<RtEntityGraphItem>> GetRtEntitiesGraphByIdAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, IReadOnlyList<OctoObjectId> rtIds,
        DataQueryOperation dataQueryOperation, IEnumerable<NavigationPair> roleIdDirectionPairs, int? skip = null, int? take = null)
    {
        if (!rtIds.Any())
        {
            return new ResultSet<RtEntityGraphItem>(new List<RtEntityGraphItem>(), 0, null);
        }

        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);

        var query =
            new SingleOriginRtQuery<RtEntityGraphItem>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                dataQueryOperation.Language);
        query.AddFieldFilters(dataQueryOperation.FieldFilters);
        query.AddIdFilter(rtIds);
        query.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        query.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        query.AddPostStagesToPipeline(dataQueryOperation.SortOrders);
        query.AddGrouping(dataQueryOperation.FieldGroupBy);
        query.AddGeospatialFilters(dataQueryOperation.GeospatialFilters);
        query.AddAssociations(roleIdDirectionPairs);

        return await query.ExecuteQuery(session, skip, take);
    }

    #endregion Data query

    #region Subscriptions

    public Task<IUpdateStream<RtEntity>> WatchRtEntitiesAsync(CkId<CkTypeId> ckTypeId,
        WatchStreamFilter watchStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return WatchRtEntitiesAsync<RtEntity>(ckTypeId, watchStreamFilter, cancellationToken);
    }

    public Task<IUpdateStream<TEntity>> WatchRtEntitiesAsync<TEntity>(WatchStreamFilter watchStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        CkId<CkTypeId> ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();
        return WatchRtEntitiesAsync<TEntity>(ckTypeId, watchStreamFilter, cancellationToken);
    }

    private async Task<IUpdateStream<TEntity>> WatchRtEntitiesAsync<TEntity>(CkId<CkTypeId> ckTypeId,
        WatchStreamFilter watchStreamFilter, CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var subscription = new Subscription<TEntity>(ckCacheService, TenantId, ckTypeGraph,
            mongoDbRepositoryDataSource);

        if (watchStreamFilter.BeforeFieldFilters != null && watchStreamFilter.BeforeFieldFilters.Any())
        {
            subscription.AddBeforeFieldFilters(watchStreamFilter.BeforeFieldFilters);
        }

        if (watchStreamFilter.FieldFilters != null && watchStreamFilter.FieldFilters.Any())
        {
            subscription.AddFieldFilters(watchStreamFilter.FieldFilters);
        }

        if (watchStreamFilter.RtId.HasValue)
        {
            subscription.AddIdFilter(new List<OctoObjectId> { watchStreamFilter.RtId.Value });
        }

        return subscription.WatchRtEntitiesAsync(watchStreamFilter.UpdateTypes, cancellationToken);
    }

    public IUpdateStream<RtAssociation> WatchToRtAssociationsAsync(CkId<CkTypeId> originCkTypeId,
        CkId<CkTypeId> targetCkTypeId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.WatchAsync(updateStreamFilter.UpdateTypes,
            () =>
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
                        "fullDocument." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                        updateStreamFilter.RoleId));
                }

                if (updateStreamFilter.OriginRtId.HasValue)
                {
                    filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocument." + nameof(RtAssociation.OriginRtId).ToCamelCase(),
                        updateStreamFilter.OriginRtId));
                }

                if (updateStreamFilter.TargetRtId.HasValue)
                {
                    filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocument." + nameof(RtAssociation.TargetRtId).ToCamelCase(),
                        updateStreamFilter.TargetRtId));
                }

                return Builders<ChangeStreamDocument<RtAssociation>>.Filter.And(filterList);
            }, () =>
            {
                var filterList = new List<FilterDefinition<ChangeStreamDocument<RtAssociation>>>
                {
                    Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocumentBeforeChange." + nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                        originCkTypeId),
                    Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocumentBeforeChange." + nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                        targetCkTypeId)
                };

                if (!string.IsNullOrWhiteSpace(updateStreamFilter.RoleId))
                {
                    filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocumentBeforeChange." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                        updateStreamFilter.RoleId));
                }

                if (updateStreamFilter.OriginRtId.HasValue)
                {
                    filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocumentBeforeChange." + nameof(RtAssociation.OriginRtId).ToCamelCase(),
                        updateStreamFilter.OriginRtId));
                }

                if (updateStreamFilter.TargetRtId.HasValue)
                {
                    filterList.Add(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                        "fullDocumentBeforeChange." + nameof(RtAssociation.TargetRtId).ToCamelCase(),
                        updateStreamFilter.TargetRtId));
                }

                return Builders<ChangeStreamDocument<RtAssociation>>.Filter.And(filterList);
            }, cancellationToken);
    }

    public IUpdateStream<RtAssociation> WatchToRtAssociationsAsync<TOriginEntity, TTargetEntity>(
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default) where TOriginEntity : RtEntity, new()
        where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetCkTypeId<TTargetEntity>();

        return WatchToRtAssociationsAsync(originCkTypeId, targetCkTypeId, updateStreamFilter, cancellationToken);
    }

    #endregion Subscriptions

    #region Advanced functionality

    public async Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session,
        CkId<CkTypeId> ckTypeId,
        string attributeName, string regexFilterValue, int takeCount)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);
        ArgumentValidation.ValidateString(nameof(regexFilterValue), regexFilterValue);

        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        if (ckTypeGraph == null)
        {
            throw InvalidCkTypeIdException.CkTypeIdNotFound(TenantId, ckTypeId);
        }

        if (ckTypeGraph.AllAttributes.All(x => x.Value.AttributeName != attributeName))
        {
            throw InvalidAttributeException.AttributeNotFound(ckTypeId, attributeName);
        }

        var match = new BsonDocument
        {
            {
                "$match",
                new BsonDocument
                {
                    {
                        $"attributes.{attributeName.ToCamelCase()}", new BsonDocument { { "$regex", regexFilterValue } }
                    }
                }
            }
        };

        var sortByCount = new BsonDocument { { "$sortByCount", $"$attributes.{attributeName.ToCamelCase()}" } };

        var limit = new BsonDocument { { "$limit", takeCount } };

        var collection = mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(ckTypeGraph);
        var result = collection.Aggregate(session,
            PipelineDefinition<RtEntity, AutoCompleteText>.Create(match, sortByCount, limit));
        return await result.ToListAsync();
    }


    public async Task UpdateAutoCompleteTexts(IOctoSession session, CkId<CkTypeId> ckTypeId, string attributeName,
        IEnumerable<object> autoCompleteValues)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        var ckType =
            await mongoDbRepositoryDataSource.CkTypes.FindSingleOrDefaultAsync(session, x => x.CkTypeId == ckTypeId);
        if (ckType == null)
        {
            throw InvalidCkTypeIdException.CkTypeIdNotFound(TenantId, ckTypeId);
        }

        var attribute = ckType.Attributes.FirstOrDefault(x => x.AttributeName == attributeName);
        if (attribute == null)
        {
            throw InvalidAttributeException.AttributeNotFound(ckTypeId, attributeName);
        }

        attribute.AutoCompleteValues = autoCompleteValues.ToList();

        try
        {
            await mongoDbRepositoryDataSource.CkTypes.ReplaceByIdAsync(session, ckType.CkTypeId, ckType);
        }
        catch (Exception e)
        {
            throw OperationFailedException.UpdateAutoCompleteTextsFailed(ckTypeId, attributeName, e);
        }
    }

    public async Task LoadCacheForTenantAsync(ICkCacheService cacheService)
    {
        await RefreshCkCacheServiceAsync(cacheService);
    }

    #endregion Advanced functionality
}
