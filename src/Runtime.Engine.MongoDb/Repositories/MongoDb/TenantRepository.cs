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

using DeleteOptions = Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class TenantRepository(
    string tenantId,
    IMetricsContext metricsContext,
    ICkCacheService ckCacheService,
    IModelLoaderService modelLoaderService,
    IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
    IBulkRtMutation bulkRtMutation)
    : RuntimeRepositoryBase(tenantId, ckCacheService, mongoDbRepositoryDataSource, bulkRtMutation), ITenantRepository
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

    protected override async Task DeleteManyRtEntitiesAsync<TEntity>(IOctoSession session, RtCkId<CkTypeId> ckTypeId,
        FieldFilterCriteria fieldFilterCriteria, DeleteOptions deleteOptions)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource, deleteOptions);
        mutation.AddFieldFilterCriteria(fieldFilterCriteria);
        await mutation.DeleteManyAsync(session).ConfigureAwait(false);
    }

    protected override async Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, RtCkId<CkTypeId> ckTypeId,
        FieldFilterCriteria fieldFilterCriteria, DeleteOptions deleteOptions)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource, deleteOptions);
        mutation.AddFieldFilterCriteria(fieldFilterCriteria);
        await mutation.DeleteOneAsync(session).ConfigureAwait(false);
    }

    protected override async Task UpdateOneRtEntityAsync<TEntity>(IOctoSession session, RtCkId<CkTypeId> ckTypeId,
        FieldFilterCriteria fieldFilterCriteria, TEntity rtEntity)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource, DeleteOptions.Default);
        mutation.AddFieldFilterCriteria(fieldFilterCriteria);
        await mutation.UpdateOneAsync(session, rtEntity).ConfigureAwait(false);
    }

    protected override async Task UpdateManyRtEntityAsync<TEntity>(IOctoSession session, RtCkId<CkTypeId> ckTypeId,
        FieldFilterCriteria fieldFilterCriteria, TEntity rtEntity)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource, DeleteOptions.Default);
        mutation.AddFieldFilterCriteria(fieldFilterCriteria);
        await mutation.UpdateManyAsync(session, rtEntity).ConfigureAwait(false);
    }

    protected override async Task ReplaceOneRtEntityAsync<TEntity>(IOctoSession session, RtCkId<CkTypeId> ckTypeId,
        FieldFilterCriteria fieldFilterCriteria, TEntity rtEntity)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var mutation = new Mutation<TEntity>(ckCacheService, TenantId, ckTypeGraph, BulkRtMutation,
            mongoDbRepositoryDataSource, DeleteOptions.Default);
        mutation.AddFieldFilterCriteria(fieldFilterCriteria);
        await mutation.ReplaceOneAsync(session, rtEntity).ConfigureAwait(false);
    }

    #endregion Data manipulation

    #region Data query

    public async Task<IResultSet<CkModel>> GetCkModelsAsync(IOctoSession session, List<CkModelId>? ckModelIds,
        RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var query = new CkModelQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(ckModelIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<CkModelId>? ckModelIds,
        IReadOnlyList<CkId<CkAttributeId>>? attributeIds,
        RtEntityQueryOptions queryOptions, int? skip = null, int? take = null)
    {
        var query = new CkAttributeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(attributeIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkModelId> ckModelIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkTypeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkId<CkTypeId>> ckTypeIds, RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var query = new CkTypeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(ckTypeIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<RtCkId<CkTypeId>> rtCkTypeIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkTypeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddRtCkIdFilter(rtCkTypeIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds,
        List<CkId<CkRecordId>>? ckRecordIds,
        RtEntityQueryOptions queryOptions, int? skip = null, int? take = null)
    {
        var query = new CkRecordQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(ckRecordIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, IReadOnlyList<CkModelId>? ckModelIds,
        List<CkId<CkEnumId>>? ckEnumIds,
        RtEntityQueryOptions queryOptions, int? skip = null, int? take = null)
    {
        var query = new CkEnumQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddIdFilter(ckEnumIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session, OctoObjectId originRtId,
        RtCkId<CkTypeId> originCkTypeId,
        RtCkId<CkAssociationRoleId> roleId,
        RtCkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null)
    {
        var result = await GetRtAssociationTargetsAsync(session, [originRtId], originCkTypeId, roleId,
            targetCkTypeId,
            graphDirection, rtIds, queryOptions, skip, take);

        return result.First().Value;
    }

    public async Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        OctoObjectId originRtId,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var result = await GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, [originRtId],
            roleId,
            graphDirection, targetRtIds, queryOptions, skip, take);

        return result.First().Value;
    }

    public async Task<IResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TTargetEntity>(IOctoSession session,
        OctoObjectId originRtId, RtCkId<CkTypeId> originCkTypeId,
        RtCkId<CkAssociationRoleId> roleId, GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds,
        RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null) where TTargetEntity : RtEntity, new()
    {
        var targetCkTypeId = RtEntityExtensions.GetRtCkTypeId<TTargetEntity>();

        var result = await GetRtAssociationTargetsAsync<TTargetEntity>(session, [originRtId], originCkTypeId, roleId,
            graphDirection,
            targetRtIds, targetCkTypeId, queryOptions, skip, take);

        return result.First().Value;
    }

    public Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, RtCkId<CkTypeId> originCkTypeId, RtCkId<CkAssociationRoleId> roleId,
        RtCkId<CkTypeId> targetCkTypeId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null)
    {
        return GetRtAssociationTargetsAsync<RtEntity>(session, originRtIds, originCkTypeId, roleId, graphDirection,
            targetRtIds, targetCkTypeId, queryOptions, skip, take);
    }

    public Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity,
        TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetRtCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetRtCkTypeId<TTargetEntity>();

        return GetRtAssociationTargetsAsync<TTargetEntity>(session, originRtIds, originCkTypeId, roleId, graphDirection,
            targetRtIds, targetCkTypeId, queryOptions, skip, take);
    }

    private async Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<
        TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, RtCkId<CkTypeId> originCkTypeId, RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, RtCkId<CkTypeId> targetCkTypeId,
        RtEntityQueryOptions queryOptions,
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
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraph);

        originHierarchicalRtQuery.AddFieldFilterCriteria(queryOptions);
        originHierarchicalRtQuery.AddIdFilter(targetRtIds);
        originHierarchicalRtQuery.AddTextSearchFilter(queryOptions.TextSearchFilter);
        originHierarchicalRtQuery.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        originHierarchicalRtQuery.AddPostStagesToPipeline(queryOptions.SortOrders);
        originHierarchicalRtQuery.AddFieldAggregation(queryOptions.FieldAggregation);
        originHierarchicalRtQuery.AddResultAggregation(queryOptions.ResultAggregation);
        originHierarchicalRtQuery.AddGeospatialFilters(queryOptions.GeospatialFilters);

        return await originHierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        OctoObjectId originRtId,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var queryOptions = RtEntityQueryOptions.Create();
        var originCkTypeId = RtEntityExtensions.GetRtCkTypeId<TOriginEntity>();
        var originRtEntityId = new RtEntityId(originCkTypeId, originRtId);

        var resultSets = await GetIndirectRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session,
            [originRtId], roleId,
            graphDirection, null, queryOptions);
        return resultSets[originRtEntityId];
    }

    public async Task<IResultSet<RtEntity>> GetIndirectRtAssociationTargetsAsync(IOctoSession session,
        RtEntityId originRtEntityId,
        RtCkId<CkAssociationRoleId> roleId, RtCkId<CkTypeId> targetCkTypeId, GraphDirections graphDirection)
    {
        var queryOptions = RtEntityQueryOptions.Create();

        var resultSets = await GetIndirectRtAssociationTargetsAsync(session, [originRtEntityId.RtId],
            originRtEntityId.CkTypeId,
            roleId,
            graphDirection, null, targetCkTypeId, queryOptions);
        return resultSets[originRtEntityId];
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetIndirectRtAssociationTargetsAsync<TOriginEntity,
        TTargetEntity>(
        IOctoSession session, IEnumerable<OctoObjectId> originRtIds,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? rtIds, RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null) where TOriginEntity : RtEntity where TTargetEntity : RtEntity, new()
    {
        var originCkTypeId = RtEntityExtensions.GetRtCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetRtCkTypeId<TTargetEntity>();

        var ckCacheService = await GetCkCacheServiceAsync();
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);
        var targetTypeGraph = await GetCkTypeGraphAsync(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectAssociationsRtQuery<TTargetEntity>(ckCacheService, TenantId,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraph);

        hierarchicalRtQuery.AddFieldFilterCriteria(queryOptions);
        hierarchicalRtQuery.AddIdFilter(rtIds);
        hierarchicalRtQuery.AddTextSearchFilter(queryOptions.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        hierarchicalRtQuery.AddPostStagesToPipeline(queryOptions.SortOrders);
        hierarchicalRtQuery.AddFieldAggregation(queryOptions.FieldAggregation);
        hierarchicalRtQuery.AddResultAggregation(queryOptions.ResultAggregation);
        hierarchicalRtQuery.AddGeospatialFilters(queryOptions.GeospatialFilters);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IMultipleOriginResultSet<RtEntity>> GetIndirectRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, RtCkId<CkTypeId> originCkTypeId,
        RtCkId<CkAssociationRoleId> roleId, GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds,
        RtCkId<CkTypeId> targetCkTypeId,
        RtEntityQueryOptions queryOptions, int? skip = null, int? take = null)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);
        var targetTypeGraph = await GetCkTypeGraphAsync(targetCkTypeId);

        var hierarchicalRtQuery =
            new MultipleOriginIndirectAssociationsRtQuery<RtEntity>(ckCacheService, TenantId,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraph);

        hierarchicalRtQuery.AddFieldFilterCriteria(queryOptions);
        hierarchicalRtQuery.AddIdFilter(targetRtIds);
        hierarchicalRtQuery.AddTextSearchFilter(queryOptions.TextSearchFilter);
        hierarchicalRtQuery.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        hierarchicalRtQuery.AddPostStagesToPipeline(queryOptions.SortOrders);
        hierarchicalRtQuery.AddFieldAggregation(queryOptions.FieldAggregation);
        hierarchicalRtQuery.AddResultAggregation(queryOptions.ResultAggregation);
        hierarchicalRtQuery.AddGeospatialFilters(queryOptions.GeospatialFilters);

        return await hierarchicalRtQuery.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<RtDeepGraphQueryResult>> GetRtDeepGraphAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds,
        RtCkId<CkTypeId> originCkTypeId,
        RtEntityQueryOptions queryOptions, int? skip = null, int? take = null)
    {
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);

        var hierarchicalDeepRtGraphQuery = new MultipleOriginHierarchicalDeepRtGraphQuery(mongoDbRepositoryDataSource,
            queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false, originRtIds,
            originTypeGraph, SystemCkIds.RtCkParentChildRoleId);
        hierarchicalDeepRtGraphQuery.AddFieldFilterCriteria(queryOptions);
        hierarchicalDeepRtGraphQuery.AddTextSearchFilter(queryOptions.TextSearchFilter);
        hierarchicalDeepRtGraphQuery.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        hierarchicalDeepRtGraphQuery.AddPostStagesToPipeline(queryOptions.SortOrders);
        hierarchicalDeepRtGraphQuery.AddFieldAggregation(queryOptions.FieldAggregation);
        hierarchicalDeepRtGraphQuery.AddResultAggregation(queryOptions.ResultAggregation);
        hierarchicalDeepRtGraphQuery.AddGeospatialFilters(queryOptions.GeospatialFilters);

        return await hierarchicalDeepRtGraphQuery.ExecuteQuery(session, skip, take);
    }

    protected override async Task<IResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        RtCkId<CkTypeId> ckTypeId,
        RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var query =
            new SingleOriginRtQuery<TEntity>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);
        query.AddGeospatialFilters(queryOptions.GeospatialFilters);

        return await query.ExecuteQuery(session, skip, take);
    }

    protected override async Task<IResultSet<TEntity>> GetRtEntitiesByIdAsync<TEntity>(IOctoSession session,
        RtCkId<CkTypeId> ckTypeId,
        IReadOnlyList<OctoObjectId> rtIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        if (!rtIds.Any())
        {
            return new ResultSet<TEntity>(new List<TEntity>(), 0, null, null);
        }

        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);

        var query =
            new SingleOriginRtQuery<TEntity>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(rtIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);
        query.AddGeospatialFilters(queryOptions.GeospatialFilters);

        return await query.ExecuteQuery(session, skip, take);
    }

    public override async Task<IResultSet<RtEntityGraphItem>> GetRtEntitiesGraphByTypeAsync(IOctoSession session,
        RtCkId<CkTypeId> ckTypeId, RtEntityQueryOptions queryOptions,
        ICollection<NavigationPair> navigationPairs, int? skip = null, int? take = null)
    {
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);

        // We analyze FieldFilters and search for navigation properties, we assign them to the correct roleIdDirectionPairs
        // and remove them from the FieldFilters
        var ckCacheService = await GetCkCacheServiceAsync();
        if (queryOptions.FieldFilters != null)
        {
            RtPathEvaluator.MergeFieldFilterToNavigationPairs(ckCacheService, TenantId, ckTypeGraph.CkTypeId,
                navigationPairs.ToArray(), queryOptions.FieldFilters);
        }

        var query =
            new SingleOriginRtQuery<RtEntityGraphItem>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);
        query.AddGeospatialFilters(queryOptions.GeospatialFilters);
        query.AddNavigationProperties(navigationPairs);

        return await query.ExecuteQuery(session, skip, take);
    }

    public override async Task<IResultSet<RtEntityGraphItem>> GetRtEntitiesGraphByIdAsync(IOctoSession session,
        RtCkId<CkTypeId> ckTypeId, IReadOnlyList<OctoObjectId> rtIds,
        RtEntityQueryOptions queryOptions, IEnumerable<NavigationPair> roleIdDirectionPairs, int? skip = null,
        int? take = null)
    {
        if (!rtIds.Any())
        {
            return new ResultSet<RtEntityGraphItem>(new List<RtEntityGraphItem>(), 0, null, null);
        }

        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);

        var query =
            new SingleOriginRtQuery<RtEntityGraphItem>(metricsContext, ckCacheService, TenantId, ckTypeGraph,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(rtIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);
        query.AddGeospatialFilters(queryOptions.GeospatialFilters);
        query.AddNavigationProperties(roleIdDirectionPairs);

        return await query.ExecuteQuery(session, skip, take);
    }

    #endregion Data query

    #region Subscriptions

    public Task<IUpdateStream<RtEntity>> WatchRtEntitiesAsync(RtCkId<CkTypeId> ckTypeId,
        WatchStreamFilter watchStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return WatchRtEntitiesAsync<RtEntity>(ckTypeId, watchStreamFilter, cancellationToken);
    }

    public Task<IUpdateStream<TEntity>> WatchRtEntitiesAsync<TEntity>(WatchStreamFilter watchStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        RtCkId<CkTypeId> ckTypeId = RtEntityExtensions.GetRtCkTypeId<TEntity>();
        return WatchRtEntitiesAsync<TEntity>(ckTypeId, watchStreamFilter, cancellationToken);
    }

    private async Task<IUpdateStream<TEntity>> WatchRtEntitiesAsync<TEntity>(RtCkId<CkTypeId> ckTypeId,
        WatchStreamFilter watchStreamFilter, CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var ckTypeGraph = await GetCkTypeGraphAsync(ckTypeId);
        var subscription = new Subscription<TEntity>(ckCacheService, TenantId, ckTypeGraph,
            mongoDbRepositoryDataSource);

        if (watchStreamFilter.BeforeFieldFilterCriteria != null)
        {
            subscription.AddBeforeFieldFilterCriteria(watchStreamFilter.BeforeFieldFilterCriteria);
        }

        if (watchStreamFilter.FieldFilterCriteria != null)
        {
            subscription.AddFieldFilterCriteria(watchStreamFilter.FieldFilterCriteria);
        }

        if (watchStreamFilter.RtId.HasValue)
        {
            subscription.AddIdFilter(new List<OctoObjectId> { watchStreamFilter.RtId.Value });
        }

        return subscription.WatchRtEntitiesAsync(watchStreamFilter.UpdateTypes, cancellationToken);
    }

    public IUpdateStream<RtAssociation> WatchToRtAssociationsAsync(RtCkId<CkTypeId> originCkTypeId,
        RtCkId<CkTypeId> targetCkTypeId,
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
        var originCkTypeId = RtEntityExtensions.GetRtCkTypeId<TOriginEntity>();
        var targetCkTypeId = RtEntityExtensions.GetRtCkTypeId<TTargetEntity>();

        return WatchToRtAssociationsAsync(originCkTypeId, targetCkTypeId, updateStreamFilter, cancellationToken);
    }

    #endregion Subscriptions

    #region Advanced functionality

    public async Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session,
        RtCkId<CkTypeId> rtCkTypeId,
        string attributeName, string regexFilterValue, int takeCount)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);
        ArgumentValidation.ValidateString(nameof(regexFilterValue), regexFilterValue);

        var cacheService = await this.GetCkCacheServiceAsync().ConfigureAwait(false);
        if (!cacheService.TryGetRtCkType(TenantId, rtCkTypeId, out var ckTypeGraph))
        {
            throw InvalidCkTypeIdException.RtCkTypeIdNotFound(TenantId, rtCkTypeId);
        }

        if (ckTypeGraph.AllAttributes.All(x => x.Value.AttributeName != attributeName))
        {
            throw InvalidAttributeException.AttributeNotFoundAtRtCkIdType(rtCkTypeId, attributeName);
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
            await mongoDbRepositoryDataSource.CkTypes.DocumentAsync(session, ckTypeId);
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

    #endregion Advanced functionality
}
