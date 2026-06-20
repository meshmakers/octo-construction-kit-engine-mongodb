using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
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
        using var session = await mongoDbRepositoryDataSource.GetSessionAsync();
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
        IReadOnlyList<CkModelId> ckModelIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkAttributeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<CkId<CkAttributeId>> ckAttributeIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkAttributeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(ckAttributeIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<RtCkId<CkAttributeId>> rtCkAttributeIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkAttributeQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddRtCkIdFilter(rtCkAttributeIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkModelId> ckModelIds,
        RtEntityQueryOptions queryOptions,
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

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session, IReadOnlyList<CkId<CkTypeId>> ckTypeIds,
        RtEntityQueryOptions queryOptions, int? skip = null,
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

    public async Task<IResultSet<CkType>> GetCkTypeAsync(IOctoSession session,
        IReadOnlyList<RtCkId<CkTypeId>> rtCkTypeIds, RtEntityQueryOptions queryOptions,
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

    public async Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session, IReadOnlyList<CkModelId> ckModelIds,
        RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkRecordQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session, List<CkId<CkRecordId>> ckRecordIds,
        RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var query = new CkRecordQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(ckRecordIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkRecord>> GetCkRecordAsync(IOctoSession session,
        List<RtCkId<CkRecordId>> rtCkRecordIds, RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var query = new CkRecordQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddRtCkIdFilter(rtCkRecordIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAssociationRole>> GetCkAssociationRoleAsync(IOctoSession session,
        IReadOnlyList<CkModelId> ckModelIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkAssociationRoleQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAssociationRole>> GetCkAssociationRoleAsync(IOctoSession session,
        List<CkId<CkAssociationRoleId>> ckAssociationRoleIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkAssociationRoleQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(ckAssociationRoleIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkAssociationRole>> GetCkAssociationRoleAsync(IOctoSession session,
        List<RtCkId<CkAssociationRoleId>> ckAssociationRoleIds, RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkAssociationRoleQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddRtCkIdFilter(ckAssociationRoleIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, IReadOnlyList<CkModelId> ckModelIds,
        RtEntityQueryOptions queryOptions,
        int? skip = null, int? take = null)
    {
        var query = new CkEnumQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddModelIdFilter(ckModelIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, List<CkId<CkEnumId>> ckEnumIds,
        RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var query = new CkEnumQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddIdFilter(ckEnumIds);
        query.AddTextSearchFilter(queryOptions.TextSearchFilter);
        query.AddAttributeSearchFilter(queryOptions.AttributeSearchFilter);
        query.AddPostStagesToPipeline(queryOptions.SortOrders);
        query.AddFieldAggregation(queryOptions.FieldAggregation);
        query.AddResultAggregation(queryOptions.ResultAggregation);

        return await query.ExecuteQuery(session, skip, take);
    }

    public async Task<IResultSet<CkEnum>> GetCkEnumAsync(IOctoSession session, List<RtCkId<CkEnumId>> rtCkEnumIds,
        RtEntityQueryOptions queryOptions, int? skip = null,
        int? take = null)
    {
        var query = new CkEnumQuery(metricsContext, mongoDbRepositoryDataSource);
        query.AddFieldFilterCriteria(queryOptions);
        query.AddRtCkIdFilter(rtCkEnumIds);
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
            targetRtIds, [targetCkTypeId], queryOptions, skip, take);

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
            targetRtIds, [targetCkTypeId], queryOptions, skip, take);
    }

    public Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, RtCkId<CkTypeId> originCkTypeId, RtCkId<CkAssociationRoleId> roleId,
        IEnumerable<RtCkId<CkTypeId>> targetCkTypeIds,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds, RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null)
    {
        return GetRtAssociationTargetsAsync<RtEntity>(session, originRtIds, originCkTypeId, roleId, graphDirection,
            targetRtIds, targetCkTypeIds, queryOptions, skip, take);
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
            targetRtIds, [targetCkTypeId], queryOptions, skip, take);
    }

    private async Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<
        TTargetEntity>(
        IOctoSession session,
        IEnumerable<OctoObjectId> originRtIds, RtCkId<CkTypeId> originCkTypeId, RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<OctoObjectId>? targetRtIds,
        IEnumerable<RtCkId<CkTypeId>> targetCkTypeIds,
        RtEntityQueryOptions queryOptions,
        int? skip = null,
        int? take = null)
        where TTargetEntity : RtEntity, new()
    {
        var ckCacheService = await GetCkCacheServiceAsync();
        var originTypeGraph = await GetCkTypeGraphAsync(originCkTypeId);

        // Build type graphs for all target types
        var targetTypeGraphs = new List<CkTypeGraph>();
        foreach (var targetCkTypeId in targetCkTypeIds)
        {
            var targetTypeGraph = await GetCkTypeGraphAsync(targetCkTypeId);
            targetTypeGraphs.Add(targetTypeGraph);
        }

        var originHierarchicalRtQuery =
            new MultipleOriginDirectAssociationsRtQuery<TTargetEntity>(ckCacheService, TenantId,
                mongoDbRepositoryDataSource,
                queryOptions.Language, queryOptions.GlobalFilter?.IncludeArchived ?? false,
                originRtIds,
                originTypeGraph, roleId, graphDirection, targetTypeGraphs);

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
        query.AddNavigationProperties(navigationPairs, queryOptions.NavigationFilterMode);

        // Use query result cache for Filter mode with navigation pairs and pagination
        if (!queryOptions.DisableCaching
            && queryOptions.NavigationFilterMode == NavigationFilterMode.Filter
            && navigationPairs.Count > 0 && (skip.HasValue || take.HasValue))
        {
            var cacheKey = QueryResultCacheService.ComputeCacheKey(ckTypeId, queryOptions, navigationPairs);
            var cacheService = ((MongoDbRepositoryDataSource)mongoDbRepositoryDataSource).CreateQueryResultCacheService();

            var cached = await cacheService.TryGetAsync(cacheKey);
            if (cached == null)
            {
                // Cache miss: collect all matching IDs and store them
                var allIds = await query.CollectMatchingEntityIds(session);
                await cacheService.StoreAsync(cacheKey, allIds);
                cached = (allIds, allIds.Count);
            }

            // Extract page from cached IDs
            var pageIds = cached.Value.EntityIds
                .Skip(skip ?? 0)
                .Take(take ?? int.MaxValue)
                .ToList();

            return await query.ExecuteEnrichmentForIds(session, pageIds, cached.Value.TotalCount);
        }

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

        if (watchStreamFilter.BeforeFieldFilterCriteria != null)
        {
            var rootCkTypeId = ckTypeGraph.DefiningCollectionRootCkTypeId ?? ckTypeGraph.CkTypeId;
            var rootGraph = ckCacheService.GetCkType(TenantId, rootCkTypeId);
            if (!rootGraph.EnableChangeStreamPreAndPostImages)
            {
                throw OperationFailedException.PreImageCaptureNotEnabled(rootCkTypeId);
            }
        }

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

    #region Migration support (CkCache-free)

    /// <inheritdoc />
    public override async Task<(IReadOnlyList<RtEntity> Entities, bool IsSharedCollection)> GetRtEntitiesByTypeForMigrationAsync(
        IOctoSession session, RtCkId<CkTypeId> rtCkTypeId)
    {
        // First, try the type's own collection (for root types that have their own collection).
        // Filter to entities whose `ckTypeId` field matches exactly: a pre-migration collection
        // root that's about to be split may hold derived-type entities in the same physical
        // collection (e.g. before the StreamData 1.1.0 → 1.2.0 split, the concrete `CkArchive`
        // collection root also stored `CkRollupArchive` entities because the latter derived from
        // the former). A bare `GetAsync` would return both kinds → the ChangeCkType migration
        // would silently mass-relabel the derived entities to the source-type's renamed target,
        // corrupting them. Filtering here preserves the user's intent that
        // `target: ckTypeId: X` means "entities tagged X", not "everything in X's collection".
        var collection = GetRtCollectionForMigration<RtEntity>(rtCkTypeId);
        var allEntities = await collection.GetAsync(session).ConfigureAwait(false);
        var ckTypeIdValue = rtCkTypeId.SemanticVersionedFullName;
        var result = allEntities
            .Where(e => e.CkTypeId?.SemanticVersionedFullName == ckTypeIdValue)
            .ToList();

        if (result.Count > 0)
        {
            return (result, false);
        }

        // Fallback: For derived types, entities are stored in a parent type's collection
        // with the ckTypeId field set to the derived type. Search all RtEntity collections.
        // Note: The ckTypeId field in MongoDB uses the semantic versioned format "ModelId/TypeName"
        // (e.g. "System.Communication/Adapter"), which omits the version suffix for version 1.
        var (collectionName, foundEntities) = await mongoDbRepositoryDataSource
            .FindEntitiesInAllCollectionsByCkTypeIdAsync<RtEntity>(session, ckTypeIdValue)
            .ConfigureAwait(false);

        if (foundEntities.Count > 0)
        {
            // Store the collection name so operations know where these entities live
            _derivedTypeCollectionMap[rtCkTypeId.FullName] = collectionName;
        }

        return (foundEntities, foundEntities.Count > 0);
    }

    /// <summary>
    /// Tracks which collection derived type entities were found in, so delete operations
    /// can target the correct collection.
    /// </summary>
    private readonly Dictionary<string, string> _derivedTypeCollectionMap = new();

    /// <inheritdoc />
    public override async Task DeleteOneRtEntityForMigrationAsync(
        IOctoSession session, RtCkId<CkTypeId> rtCkTypeId, OctoObjectId rtId)
    {
        // If we previously found this type's entities in a parent collection, delete from there
        if (_derivedTypeCollectionMap.TryGetValue(rtCkTypeId.FullName, out var parentCollectionName))
        {
            var parentCollection = GetRtCollectionByName<RtEntity>(parentCollectionName);
            await parentCollection.DeleteOneAsync(session, rtId).ConfigureAwait(false);
            return;
        }

        // Default: delete from the type's own collection
        var collection = GetRtCollectionForMigration<RtEntity>(rtCkTypeId);
        await collection.DeleteOneAsync(session, rtId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task InsertOneRtEntityForMigrationAsync(
        IOctoSession session, RtCkId<CkTypeId> rtCkTypeId, RtEntity rtEntity)
    {
        rtEntity.CkTypeId = rtCkTypeId;
        rtEntity.RtCreationDateTime ??= DateTime.UtcNow;
        rtEntity.RtChangedDateTime = DateTime.UtcNow;

        // Route to the target type's defining collection root if the CkCache has the target type
        // loaded. This matters when a ChangeCkType migration renames a former collection-root
        // type (e.g. `CkArchive`) to a derived subtype (`RawArchive`) under a newly-introduced
        // abstract base (`Archive`): the runtime collection-name convention now puts every
        // derived archive into `RtEntity_SystemStreamDataArchive`, not the per-concrete-type
        // collection that the legacy `GetRtCollectionForMigration` would target. Writing to the
        // wrong physical collection silently hides the migrated entities from the regular query
        // path (which goes through `DefiningCollectionRootCkTypeId`). If the cache is mid-import
        // and doesn't have the target type yet, fall back to the per-type collection — that's
        // the legacy behaviour and covers the common case where the target type IS its own
        // collection root.
        var ckCacheService = await GetCkCacheServiceAsync().ConfigureAwait(false);
        if (ckCacheService.TryGetRtCkType(TenantId, rtCkTypeId, out var graph)
            && graph.DefiningCollectionRootCkTypeId is { } collectionRoot
            && collectionRoot.SemanticVersionedFullName != rtCkTypeId.SemanticVersionedFullName)
        {
            var rootCollection = mongoDbRepositoryDataSource
                .GetRtDatabaseCollectionByTypeId<RtEntity>(collectionRoot.ToRtCkId());
            await rootCollection.InsertOneAsync(session, rtEntity).ConfigureAwait(false);
            return;
        }

        var collection = GetRtCollectionForMigration<RtEntity>(rtCkTypeId);
        await collection.InsertOneAsync(session, rtEntity).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task UpdateCkTypeIdForMigrationAsync(
        IOctoSession session, OctoObjectId rtId, RtCkId<CkTypeId> newCkTypeId)
    {
        // Find which collection actually contains this entity by searching all known
        // parent collections from the derived type map
        IMongoDbDataSourceCollection<OctoObjectId, RtEntity>? collection = null;
        var idFilter = Builders<RtEntity>.Filter.Eq("_id", rtId);
        var checkedCollectionNames = new HashSet<string>();

        foreach (var kvp in _derivedTypeCollectionMap)
        {
            if (!checkedCollectionNames.Add(kvp.Value))
            {
                continue;
            }

            var candidateCollection = GetRtCollectionByName<RtEntity>(kvp.Value);
            var existsInCollection = await candidateCollection.GetTotalCountAsync(session, idFilter)
                .ConfigureAwait(false);
            if (existsInCollection > 0)
            {
                collection = candidateCollection;
                break;
            }
        }

        collection ??= GetRtCollectionForMigration<RtEntity>(newCkTypeId);

        // Use semantic versioned format to match the MongoDB storage format
        var update = Builders<RtEntity>.Update.Set("ckTypeId", newCkTypeId.SemanticVersionedFullName);
        await collection.UpdateOneAsync(session, rtId, update).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<int> UpdateAssociationCkTypeIdsForMigrationAsync(
        IOctoSession session, RtCkId<CkTypeId> oldCkTypeId, RtCkId<CkTypeId> newCkTypeId)
    {
        var oldValue = oldCkTypeId.SemanticVersionedFullName;
        var newValue = newCkTypeId.SemanticVersionedFullName;
        var associations = mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations;
        int totalUpdated = 0;

        // Count matches before updating so the returned count reflects actually targeted rows
        var originFilter = Builders<RtAssociation>.Filter.Eq("originCkTypeId", oldValue);
        var originCount = await associations.GetTotalCountAsync(session, originFilter).ConfigureAwait(false);
        var originUpdate = Builders<RtAssociation>.Update.Set("originCkTypeId", newValue);
        await associations.UpdateManyAsync(session, originFilter, originUpdate).ConfigureAwait(false);

        var targetFilter = Builders<RtAssociation>.Filter.Eq("targetCkTypeId", oldValue);
        var targetCount = await associations.GetTotalCountAsync(session, targetFilter).ConfigureAwait(false);
        var targetUpdate = Builders<RtAssociation>.Update.Set("targetCkTypeId", newValue);
        await associations.UpdateManyAsync(session, targetFilter, targetUpdate).ConfigureAwait(false);

        totalUpdated = (int)(originCount + targetCount);
        return totalUpdated;
    }

    /// <inheritdoc />
    public override async Task<bool> DropCollectionIfEmptyForMigrationAsync(RtCkId<CkTypeId> rtCkTypeId)
    {
        var collection = GetRtCollectionForMigration<RtEntity>(rtCkTypeId);
        using var session = await mongoDbRepositoryDataSource.GetSessionAsync().ConfigureAwait(false);
        var count = await collection.GetTotalCountAsync(session).ConfigureAwait(false);

        if (count > 0)
        {
            return false;
        }

        await mongoDbRepositoryDataSource.DropRtDatabaseCollectionByTypeIdAsync(rtCkTypeId).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public override async Task RewriteAttributeValueForMigrationAsync(
        IOctoSession session,
        RtCkId<CkTypeId> rtCkTypeId,
        OctoObjectId rtId,
        string attributeId,
        object? newValue)
    {
        // Resolve the collection the same way DeleteOneRtEntityForMigrationAsync does so that
        // derived-type entities living in a parent collection are correctly addressed.
        IMongoDbDataSourceCollection<OctoObjectId, RtEntity> collection;
        if (_derivedTypeCollectionMap.TryGetValue(rtCkTypeId.FullName, out var parentCollectionName))
        {
            collection = GetRtCollectionByName<RtEntity>(parentCollectionName);
        }
        else
        {
            collection = GetRtCollectionForMigration<RtEntity>(rtCkTypeId);
        }

        // Attribute storage uses the same convention as MongoDataSourceMapper.ApplyUpdate:
        // `attributes.<key.ToCamelCase()>`. The CK migration step's attribute id is the
        // dictionary key on the in-memory RtEntity; the camelCase transform produces the BSON
        // field path. rtVersion is bumped and rtChangedDateTime updated to match the regular
        // mutation path's change-tracking semantics — otherwise downstream change-stream
        // consumers would miss the rewrite.
        var fieldPath = "attributes." + attributeId.ToCamelCase();
        var updateDef = Builders<RtEntity>.Update.Combine(
            Builders<RtEntity>.Update.Set(fieldPath, newValue),
            Builders<RtEntity>.Update.Set("rtChangedDateTime", DateTime.UtcNow),
            Builders<RtEntity>.Update.Inc("rtVersion", 1));

        await collection.UpdateOneAsync(session, rtId, updateDef).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a MongoDB collection for a CK type by directly constructing the collection name
    /// from the type ID, without requiring a CkTypeGraph from the CK cache.
    /// This assumes the type is a root collection type (not a derived type stored in a parent's collection).
    /// </summary>
    private IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtCollectionForMigration<TEntity>(
        RtCkId<CkTypeId> rtCkTypeId) where TEntity : RtEntity, new()
    {
        return mongoDbRepositoryDataSource.GetRtDatabaseCollectionByTypeId<TEntity>(rtCkTypeId);
    }

    /// <summary>
    /// Gets a MongoDB collection by its full collection name (e.g. "RtEntity_SystemCommunicationDeployableEntity").
    /// </summary>
    private IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtCollectionByName<TEntity>(
        string collectionName) where TEntity : RtEntity, new()
    {
        var suffix = collectionName.StartsWith("RtEntity_")
            ? collectionName.Substring("RtEntity_".Length)
            : collectionName;
        return mongoDbRepositoryDataSource.GetRtDatabaseCollectionByCollectionSuffix<TEntity>(suffix);
    }

    #endregion Migration support (CkCache-free)
}
