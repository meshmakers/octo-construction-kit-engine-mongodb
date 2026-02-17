using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class MultipleOriginDirectAssociationsRtQuery : MultipleOriginDirectAssociationsRtQuery<RtEntity>
{
    internal MultipleOriginDirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, bool includeArchivedEntities, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkTypeGraph targetCkTypeGraph)
        : base(ckCacheService, tenantId, mongoDbRepositoryDataSource, language, includeArchivedEntities, rtIds,
            originCkTypeGraph, roleId,
            graphDirection,
            [targetCkTypeGraph])
    {
    }

    internal MultipleOriginDirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, bool includeArchivedEntities, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<CkTypeGraph> targetCkTypeGraphs)
        : base(ckCacheService, tenantId, mongoDbRepositoryDataSource, language, includeArchivedEntities, rtIds,
            originCkTypeGraph, roleId,
            graphDirection,
            targetCkTypeGraphs)
    {
    }
}

internal class MultipleOriginDirectAssociationsRtQuery<TTargetEntity> : Query<TTargetEntity>
    where TTargetEntity : RtEntity, new()
{
    private readonly GraphDirections _graphDirection;
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly bool _includeArchivedEntities;
    private readonly CkTypeGraph _originCkTypeGraph;
    private readonly RtCkId<CkAssociationRoleId> _roleId;
    private readonly IEnumerable<OctoObjectId> _rtIds;
    private readonly IReadOnlyList<CkTypeGraph> _targetCkTypeGraphs;
    private readonly List<IPipelineStageDefinition> _geospatialFilters;

    internal MultipleOriginDirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, bool includeArchivedEntities, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, IReadOnlyList<CkTypeGraph> targetCkTypeGraphs)
        : base(new RtEntityFieldFilterResolver<TTargetEntity>(ckCacheService, tenantId, targetCkTypeGraphs[0]), language)
    {
        ArgumentNullException.ThrowIfNull(targetCkTypeGraphs);
        if (targetCkTypeGraphs.Count == 0)
        {
            throw new ArgumentException("At least one target type graph is required", nameof(targetCkTypeGraphs));
        }

        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _includeArchivedEntities = includeArchivedEntities;
        _rtIds = rtIds;
        _originCkTypeGraph = originCkTypeGraph;
        _roleId = roleId;
        _graphDirection = graphDirection;
        _targetCkTypeGraphs = targetCkTypeGraphs;
        _geospatialFilters = new List<IPipelineStageDefinition>();
    }

    protected override void AddPreStagesToPipelines(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        _geospatialFilters.ForEach(pipelineStageDefinitions.Add);

        // Ensure that archived entities are not added to the result if defined.
        if (!_includeArchivedEntities)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(
                Builders<TTargetEntity>.Filter.Ne(ckType => ckType.RtState, RtState.Archived)
            ));
        }

        base.AddPostStagesToPipeline(pipelineStageDefinitions);
    }

    /// <summary>
    /// Add a geospatial filters to the query
    /// </summary>
    /// <param name="geospatialFilters">Filters to add</param>
    public void AddGeospatialFilters(ICollection<GeospatialFilter>? geospatialFilters)
    {
        if (geospatialFilters == null)
        {
            return;
        }

        foreach (var geospatialFilter in geospatialFilters)
        {
            var resolvedAttributeName = _fieldFilterResolver.ResolveAttributePath(geospatialFilter.AttributeName);
            if (string.IsNullOrWhiteSpace(resolvedAttributeName))
            {
                throw OperationFailedException.AttributePathResolutionFailed(geospatialFilter.AttributeName);
            }

            if (geospatialFilter is NearGeospatialFilter nearGeospatialFilter)
            {
                GeoJsonPoint<GeoJsonCoordinates> point = nearGeospatialFilter.Point.ToGeoJsonPoint();

                _geospatialFilters.Add(OctoPipelineStageBuilder.GeoNear<RtEntity, GeoJsonCoordinates>(
                    resolvedAttributeName, point, nearGeospatialFilter.MinDistance, nearGeospatialFilter.MaxDistance));
            }
        }
    }

    internal async Task<IMultipleOriginResultSet<TTargetEntity>> ExecuteQuery(IOctoSession session, int? skip,
        int? take)
    {
        if (skip.HasValue && !take.HasValue)
        {
            throw OperationFailedException.PagingNeeded();
        }

        // Field names for the association lookup
        var innerLocalFieldRtIdName = "targetRtId";
        var innerLocalFieldCkIdName = "targetCkTypeId";
        var connectToField = (FieldDefinition<RtAssociation, string>)"originRtId";

        switch (_graphDirection)
        {
            case GraphDirections.Inbound:
                innerLocalFieldRtIdName = "originRtId";
                innerLocalFieldCkIdName = "originCkTypeId";
                connectToField = "targetRtId";
                break;
            case GraphDirections.Outbound:
                break;
            default:
                throw OperationFailedException.GraphDirectionUnsupported(_graphDirection);
        }

        var connectFromField = (FieldDefinition<RtEntity, string[]>)"_id";
        var @as = (FieldDefinition<BsonDocument, TTargetEntity[]>)"_associations";

        // Collect all derived types from all target type graphs
        var allTargetCkIds = _targetCkTypeGraphs
            .SelectMany(graph => graph.GetAllDerivedTypes(true))
            .Select(t => t.ToRtCkId())
            .Distinct()
            .ToList();

        // Group target type graphs by their collection root
        var collectionGroups = _targetCkTypeGraphs
            .GroupBy(g => g.DefiningCollectionRootCkTypeId)
            .ToList();

        // Build the association filter (matches all target types across all collections)
        List<FilterDefinition<RtAssociation>> associationFilter =
        [
            Builders<RtAssociation>.Filter.Eq("associationRoleId", _roleId),
            Builders<RtAssociation>.Filter.In(innerLocalFieldCkIdName, allTargetCkIds)
        ];

        if (!_includeArchivedEntities)
        {
            associationFilter.Add(Builders<RtAssociation>.Filter.Ne(ckType => ckType.RtState, RtState.Archived));
        }

        // Build the sub-pipeline using BsonDocument stages for flexibility with multiple lookups
        var associationRenderArgs = new RenderArgs<RtAssociation>(
            BsonSerializer.SerializerRegistry.GetSerializer<RtAssociation>(),
            BsonSerializer.SerializerRegistry);

        var pipelineStages = new List<BsonDocument>
        {
            // Match associations
            new("$match", Builders<RtAssociation>.Filter.And(associationFilter).Render(associationRenderArgs))
        };

        // Pre-render field filters (used by both paths)
        BsonDocument? renderedFieldFilter = null;
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            var filterRenderArgs = new RenderArgs<TTargetEntity>(
                BsonSerializer.SerializerRegistry.GetSerializer<TTargetEntity>(),
                BsonSerializer.SerializerRegistry);
            renderedFieldFilter = filterDefinitions.Render(filterRenderArgs);
        }

        var sortStage = GetSortStageBsonDocument();

        if (HasSortDefinitions && take.HasValue && _geospatialFilters.Count == 0)
        {
            // OPTIMIZED PATH: Collect target IDs via $group, then batched $lookup with sort+limit.
            // Instead of N individual $lookups (one per association), this uses a single $in query
            // per collection group, which is significantly faster when there are many associations
            // (e.g. 2000 associations with first:1 sort:startedAt DESC).

            var limitValue = (skip ?? 0) + take.Value + 1;

            // $group: Collect unique target IDs and count associations
            pipelineStages.Add(new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { "targetIds", new BsonDocument("$addToSet", $"${innerLocalFieldRtIdName}") },
                { "assocCount", new BsonDocument("$sum", 1) }
            }));

            // Per collection group: $lookup with inner pipeline using $in for batched matching
            var collectionIndex = 0;
            foreach (var group in collectionGroups)
            {
                var groupGraphs = group.ToList();
                var targetFieldName = $"target{collectionIndex}";
                var collectionName = _mongoDbRepositoryDataSource
                    .GetRtDatabaseCollection<TTargetEntity>(groupGraphs[0])
                    .GetMongoCollection().CollectionNamespace.CollectionName;

                var innerPipeline = new BsonArray();

                // Match targets by ID using $in with the collected target IDs
                innerPipeline.Add(new BsonDocument("$match", new BsonDocument("$expr",
                    new BsonDocument("$in", new BsonArray { "$_id", "$$tids" }))));

                // Archived filter on target entities
                if (!_includeArchivedEntities)
                {
                    innerPipeline.Add(new BsonDocument("$match",
                        new BsonDocument("rtState", new BsonDocument("$ne", (int)RtState.Archived))));
                }

                // Field filters
                if (renderedFieldFilter != null)
                {
                    innerPipeline.Add(new BsonDocument("$match", renderedFieldFilter));
                }

                // Sort inside the lookup
                if (sortStage != null)
                {
                    innerPipeline.Add(sortStage);
                }

                // Count total matching entities before applying limit for correct totalCount.
                // Without this, totalCount would reflect the limited page size instead of the real total.
                innerPipeline.Add(new BsonDocument("$setWindowFields", new BsonDocument("output",
                    new BsonDocument("_assocCount", new BsonDocument
                    {
                        { "$sum", 1 },
                        { "window", new BsonDocument("documents", new BsonArray { "unbounded", "unbounded" }) }
                    }))));

                innerPipeline.Add(new BsonDocument("$limit", limitValue));

                pipelineStages.Add(new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", collectionName },
                    { "let", new BsonDocument("tids", "$targetIds") },
                    { "pipeline", innerPipeline },
                    { "as", targetFieldName }
                }));

                collectionIndex++;
            }

            // For multiple collection groups, compute combined total count across all collections
            if (collectionGroups.Count > 1)
            {
                var addExpr = new BsonArray();
                for (var i = 0; i < collectionGroups.Count; i++)
                {
                    addExpr.Add(new BsonDocument("$ifNull", new BsonArray
                    {
                        new BsonDocument("$arrayElemAt", new BsonArray { $"$target{i}._assocCount", 0 }),
                        new BsonDocument("$size", $"$target{i}")
                    }));
                }

                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("_totalAssocCount", new BsonDocument("$add", addExpr))));
            }

            // Combine target arrays from all collection groups
            if (collectionGroups.Count > 1)
            {
                var concatArrays = new BsonArray();
                for (var i = 0; i < collectionGroups.Count; i++)
                {
                    concatArrays.Add($"$target{i}");
                }

                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("target", new BsonDocument("$concatArrays", concatArrays))));

                // Override per-collection _assocCount with combined total on each target element
                pipelineStages.Add(new BsonDocument("$addFields", new BsonDocument("target",
                    new BsonDocument("$map", new BsonDocument
                    {
                        { "input", "$target" },
                        { "as", "t" },
                        {
                            "in", new BsonDocument("$mergeObjects", new BsonArray
                            {
                                "$$t",
                                new BsonDocument("_assocCount", "$_totalAssocCount")
                            })
                        }
                    }))));
            }
            else
            {
                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("target", "$target0")));
            }

            // Unwind and replace root to produce flat target entity documents
            pipelineStages.Add(new BsonDocument("$unwind", "$target"));
            pipelineStages.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$target")));

            // For multiple collections, re-sort and re-limit the combined results
            if (collectionGroups.Count > 1)
            {
                if (sortStage != null)
                {
                    pipelineStages.Add(sortStage);
                }

                pipelineStages.Add(new BsonDocument("$limit", limitValue));
            }
        }
        else
        {
            // STANDARD PATH: Individual target lookups per association using localField/foreignField.
            // Used when no sort is defined, no take limit, or geospatial filters are active.
            // This is faster than let/pipeline with $expr because MongoDB can use the
            // _id index directly with its native join optimization.
            var collectionIndex = 0;
            foreach (var group in collectionGroups)
            {
                var groupGraphs = group.ToList();
                var targetFieldName = $"target{collectionIndex}";

                pipelineStages.Add(new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TTargetEntity>(groupGraphs[0]).GetMongoCollection().CollectionNamespace.CollectionName },
                    { "localField", innerLocalFieldRtIdName },
                    { "foreignField", "_id" },
                    { "as", targetFieldName }
                }));

                collectionIndex++;
            }

            // Combine all target arrays using $concatArrays
            if (collectionGroups.Count > 1)
            {
                var concatArrays = new BsonArray();
                for (var i = 0; i < collectionGroups.Count; i++)
                {
                    concatArrays.Add($"$target{i}");
                }

                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("target", new BsonDocument("$concatArrays", concatArrays))));
            }
            else
            {
                // Single collection - just rename target0 to target
                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("target", "$target0")));
            }

            // Unwind the combined targets and replace root
            pipelineStages.Add(new BsonDocument("$unwind", "$target"));
            pipelineStages.Add(new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$target")));

            // Add archived filter if needed
            if (!_includeArchivedEntities)
            {
                pipelineStages.Add(new BsonDocument("$match",
                    new BsonDocument("rtState", new BsonDocument("$ne", (int)RtState.Archived))));
            }

            // Add field filters if any
            if (renderedFieldFilter != null)
            {
                pipelineStages.Add(new BsonDocument("$match", renderedFieldFilter));
            }

            // Add sort stage if sort definitions are configured
            if (sortStage != null)
            {
                pipelineStages.Add(sortStage);
            }

            // When pagination is specified, limit results within the lookup pipeline
            // to avoid materializing all associated entities. Fetch one extra beyond
            // the requested page to enable correct hasNextPage detection.
            if (take.HasValue)
            {
                var limitValue = (skip ?? 0) + take.Value + 1;

                // Count total matching entities before applying limit for correct totalCount
                pipelineStages.Add(new BsonDocument("$setWindowFields", new BsonDocument("output",
                    new BsonDocument("_assocCount", new BsonDocument
                    {
                        { "$sum", 1 },
                        { "window", new BsonDocument("documents", new BsonArray { "unbounded", "unbounded" }) }
                    }))));

                pipelineStages.Add(new BsonDocument("$limit", limitValue));
            }
        }

        // Create the pipeline from BsonDocuments with correct type chain:
        // RtAssociation -> BsonDocument -> ... -> BsonDocument -> TTargetEntity -> TTargetEntity -> ...
        // Find the $replaceRoot stage index - after this stage, documents are TTargetEntity type
        var replaceRootIndex = pipelineStages.FindIndex(stage =>
            stage.Contains("$replaceRoot") || stage.Contains("$replaceWith"));

        var typedStages = new List<IPipelineStageDefinition>();

        // First stage takes RtAssociation as input
        if (pipelineStages.Count > 0)
        {
            typedStages.Add(new BsonDocumentPipelineStageDefinition<RtAssociation, BsonDocument>(pipelineStages[0]));
        }

        // Middle stages before $replaceRoot are BsonDocument -> BsonDocument
        for (var i = 1; i < replaceRootIndex; i++)
        {
            typedStages.Add(new BsonDocumentPipelineStageDefinition<BsonDocument, BsonDocument>(pipelineStages[i]));
        }

        // $replaceRoot stage outputs TTargetEntity
        if (replaceRootIndex >= 0 && replaceRootIndex < pipelineStages.Count)
        {
            typedStages.Add(new BsonDocumentPipelineStageDefinition<BsonDocument, TTargetEntity>(pipelineStages[replaceRootIndex]));
        }

        // Stages after $replaceRoot are TTargetEntity -> TTargetEntity
        for (var i = replaceRootIndex + 1; i < pipelineStages.Count; i++)
        {
            typedStages.Add(new BsonDocumentPipelineStageDefinition<TTargetEntity, TTargetEntity>(pipelineStages[i]));
        }

        var pipelineDefinition = PipelineDefinition<RtAssociation, TTargetEntity>.Create(
            typedStages,
            BsonSerializer.LookupSerializer<TTargetEntity>());

        var aggregate = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph)
            .Aggregate(session)
            .Match(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds))
            .Lookup(
                _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                connectFromField,
                connectToField,
                pipelineDefinition,
                @as
            );

        // Use _assocCount from $setWindowFields if available (when $limit is in the pipeline),
        // otherwise fall back to array size (when no $limit, all entities are in the array)
        var totalCountExpr = "{$ifNull: [{$arrayElemAt: ['$_associations._assocCount', 0]}, {$size: '$_associations'}]}";

        // When pagination is active, strip the internal _assocCount field from target entities
        // to prevent BSON deserialization errors (the field is only needed for totalCount calculation)
        var aggregate2 = aggregate.ReplaceWith(
            (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)
            $"{{ _id: {{'rtId': '$_id', 'ckTypeId': '$ckTypeId' }}, totalCount: {totalCountExpr}, 'targets': '$_associations'}}");

        if (skip.HasValue)
        {
            var targetsExpr =
                $"{{$map: {{input: {{$slice: ['$_associations', {skip},{take}]}}, as: 'e', in: {{$unsetField: {{field: '_assocCount', input: '$$e'}}}}}}}}";
            var query =
                $"{{ _id: {{'rtId': '$_id', 'ckTypeId': '$ckTypeId' }}, totalCount: {totalCountExpr}, 'targets': {targetsExpr}}}";
            aggregate2 = aggregate.ReplaceWith(
                (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)query);
        }
        else if (take.HasValue)
        {
            var targetsExpr =
                $"{{$map: {{input: {{$slice: ['$_associations', 0,{take}]}}, as: 'e', in: {{$unsetField: {{field: '_assocCount', input: '$$e'}}}}}}}}";
            var query =
                $"{{ _id: {{'rtId': '$_id', 'ckTypeId': '$ckTypeId' }}, totalCount: {totalCountExpr}, 'targets': {targetsExpr}}}";
            aggregate2 = aggregate.ReplaceWith(
                (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)query);
        }

        var result = await aggregate2.ToListAsync();

        foreach (var multipleResult in result)
        {
            var targetEntities = CalculateAggregations(multipleResult.Targets);
            multipleResult.AggregationResult = targetEntities.Item1;
            multipleResult.FieldAggregationResults = targetEntities.Item2;
        }

        return new MultipleOriginResultSet<TTargetEntity>(result);
    }


    protected override (AggregationResult?, IEnumerable<FieldAggregationResult>?) CalculateAggregations(
        IEnumerable<TTargetEntity> resultList)
    {
        if (ResultAggregation == null && FieldAggregation == null)
        {
            return (null, null);
        }

        var statisticFunctions =
            new RtStatisticFunctions<TTargetEntity>(_ckCacheService, _tenantId, ResultAggregation, FieldAggregation);
        IEnumerable<TTargetEntity> targetEntities = resultList as TTargetEntity[] ?? resultList.ToArray();
        var fieldAggregationResults = statisticFunctions.CalculateFieldAggregation(targetEntities);
        var resultAggregation = statisticFunctions.CalculateResultAggregation(targetEntities);
        return (resultAggregation, fieldAggregationResults);
    }
}
