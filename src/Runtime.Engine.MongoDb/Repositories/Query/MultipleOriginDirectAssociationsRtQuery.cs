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
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

/// <summary>
///     Tuning knobs for the adaptive association navigation (AB#4369). Non-generic on purpose —
///     a static on the generic query class would exist once per closed target type.
/// </summary>
internal static class ReverseNavigationOptions
{
    /// <summary>
    ///     Edge count per origin above which a sorted+limited navigation switches from the
    ///     edge-driven lookup to the reversed, entity-driven query. The edge-driven optimized
    ///     path collects every target id into a single $addToSet array and probes it with a
    ///     non-indexable $expr $in — fine for thousands of edges, catastrophic for hundreds of
    ///     thousands (observed: a pipeline with 650k execution edges timed out the Studio
    ///     data-flow page). The reversed path walks the target collection in sort order
    ///     (index-backed) and probes edge existence per candidate, so its cost scales with the
    ///     page size for dense origins — but with the collection size for sparse ones, hence
    ///     the adaptive split. Mutable for tests only.
    /// </summary>
    internal static int EdgeThreshold = 10_000;
}

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

        // A page size of 0 is the count-only use case (children(first: 0) { totalCount },
        // and a nested selection of totalCount alone, which the asset repository maps to take 0). It runs
        // a count that loads no target document; skip and sort do not change a total count.
        if (take == 0)
        {
            return await ExecuteCountOnlyAsync(session);
        }

        return await ExecuteQueryCore(session, skip, take);
    }

    private (string InnerLocalFieldRtIdName, string InnerLocalFieldCkIdName, string ConnectToFieldName)
        GetDirectionFieldNames()
    {
        return _graphDirection switch
        {
            GraphDirections.Inbound => ("originRtId", "originCkTypeId", "targetRtId"),
            GraphDirections.Outbound => ("targetRtId", "targetCkTypeId", "originRtId"),
            _ => throw OperationFailedException.GraphDirectionUnsupported(_graphDirection)
        };
    }

    private List<RtCkId<CkTypeId>> GetAllTargetCkIds()
    {
        // Collect all derived types from all target type graphs
        return _targetCkTypeGraphs
            .SelectMany(graph => graph.GetAllDerivedTypes(true))
            .Select(t => t.ToRtCkId())
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     The <c>$match</c> on the association collection that every path starts its sub-pipeline with: the role,
    ///     the target types across all collections and, unless archived entities are included, no archived edge.
    /// </summary>
    private BsonDocument BuildAssociationMatchStage(string innerLocalFieldCkIdName,
        IReadOnlyList<RtCkId<CkTypeId>> allTargetCkIds)
    {
        List<FilterDefinition<RtAssociation>> associationFilter =
        [
            Builders<RtAssociation>.Filter.Eq("associationRoleId", _roleId),
            Builders<RtAssociation>.Filter.In(innerLocalFieldCkIdName, allTargetCkIds)
        ];

        if (!_includeArchivedEntities)
        {
            associationFilter.Add(Builders<RtAssociation>.Filter.Ne(ckType => ckType.RtState, RtState.Archived));
        }

        var associationRenderArgs = new RenderArgs<RtAssociation>(
            BsonSerializer.SerializerRegistry.GetSerializer<RtAssociation>(),
            BsonSerializer.SerializerRegistry);

        return new BsonDocument("$match",
            Builders<RtAssociation>.Filter.And(associationFilter).Render(associationRenderArgs));
    }

    /// <summary>
    ///     The security, id and field filters on the target entities, rendered once, or null when there is none.
    /// </summary>
    private BsonDocument? RenderTargetFieldFilter()
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            return null;
        }

        var filterRenderArgs = new RenderArgs<TTargetEntity>(
            BsonSerializer.SerializerRegistry.GetSerializer<TTargetEntity>(),
            BsonSerializer.SerializerRegistry);
        return filterDefinitions.Render(filterRenderArgs);
    }

    /// <summary>
    ///     The sub-pipeline over the association collection of the count-only path. It counts exactly what
    ///     the standard path returns as totalCount - one per edge whose target exists in one of the target
    ///     collections, is not archived (unless included) and passes the security, id and field filters - but no
    ///     target document leaves its collection: the per-edge target <c>$lookup</c> filters inside its own
    ///     pipeline and ends with <c>$project: { _id: 1 }</c> (at most one id per edge), and the sub-pipeline ends
    ///     with <c>$count</c>, so the origin receives a single count document instead of an array of targets or
    ///     ids. No sort, no paging, no <c>$setWindowFields</c>: a total count does not depend on them. The edge
    ///     <c>$lookup</c> from the origins joins on the indexed originRtId / targetRtId field, every target probe on
    ///     <c>_id</c>.
    /// </summary>
    internal IReadOnlyList<BsonDocument> CreateCountOnlyAssociationStages()
    {
        var (innerLocalFieldRtIdName, innerLocalFieldCkIdName, _) = GetDirectionFieldNames();
        var collectionGroups = _targetCkTypeGraphs
            .GroupBy(g => g.DefiningCollectionRootCkTypeId)
            .ToList();
        var renderedFieldFilter = RenderTargetFieldFilter();

        var stages = new List<BsonDocument>
        {
            BuildAssociationMatchStage(innerLocalFieldCkIdName, GetAllTargetCkIds())
        };

        var collectionIndex = 0;
        foreach (var group in collectionGroups)
        {
            var targetPipeline = new BsonArray();
            if (!_includeArchivedEntities)
            {
                targetPipeline.Add(new BsonDocument("$match",
                    new BsonDocument("rtState", new BsonDocument("$ne", (int)RtState.Archived))));
            }

            if (renderedFieldFilter != null)
            {
                targetPipeline.Add(new BsonDocument("$match", renderedFieldFilter));
            }

            targetPipeline.Add(new BsonDocument("$project", new BsonDocument("_id", 1)));

            stages.Add(new BsonDocument("$lookup", new BsonDocument
            {
                {
                    "from", _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TTargetEntity>(group.First())
                        .GetMongoCollection().CollectionNamespace.CollectionName
                },
                { "localField", innerLocalFieldRtIdName },
                { "foreignField", "_id" },
                { "pipeline", targetPipeline },
                { "as", $"target{collectionIndex}" }
            }));

            collectionIndex++;
        }

        if (collectionGroups.Count > 1)
        {
            var concatArrays = new BsonArray();
            for (var i = 0; i < collectionGroups.Count; i++)
            {
                concatArrays.Add($"$target{i}");
            }

            stages.Add(new BsonDocument("$project",
                new BsonDocument("target", new BsonDocument("$concatArrays", concatArrays))));
        }
        else
        {
            stages.Add(new BsonDocument("$project", new BsonDocument("target", "$target0")));
        }

        // One document per edge and found target, exactly as the standard path unwinds, then counted.
        stages.Add(new BsonDocument("$unwind", "$target"));
        stages.Add(new BsonDocument("$count", "_assocCount"));
        return stages;
    }

    /// <summary>
    ///     Called with the association sub-pipeline each time the count-only form runs, so that tests can prove
    ///     which pipeline a query executes (the same pattern as <c>SingleOriginQuery.PipelineObserver</c>).
    /// </summary>
    internal Action<IReadOnlyList<BsonDocument>>? CountOnlyPipelineObserver { get; set; }

    private async Task<IMultipleOriginResultSet<TTargetEntity>> ExecuteCountOnlyAsync(IOctoSession session)
    {
        var (_, _, connectToFieldName) = GetDirectionFieldNames();
        var stages = CreateCountOnlyAssociationStages();
        CountOnlyPipelineObserver?.Invoke(stages);

        var typedStages = new List<IPipelineStageDefinition>
        {
            new BsonDocumentPipelineStageDefinition<RtAssociation, BsonDocument>(stages[0])
        };
        typedStages.AddRange(stages.Skip(1)
            .Select(stage => new BsonDocumentPipelineStageDefinition<BsonDocument, BsonDocument>(stage)));

        var pipelineDefinition = PipelineDefinition<RtAssociation, BsonDocument>.Create(
            typedStages, BsonDocumentSerializer.Instance);

        // An origin without a matching edge gets an empty array (no $count document): total 0.
        var result = await _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph)
            .Aggregate(session)
            .Match(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds))
            .Lookup(
                _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                (FieldDefinition<RtEntity, string[]>)"_id",
                (FieldDefinition<RtAssociation, string>)connectToFieldName,
                pipelineDefinition,
                (FieldDefinition<BsonDocument, BsonDocument[]>)"_associations")
            .ReplaceWith((AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)
                "{ _id: { 'rtId': '$_id', 'ckTypeId': '$ckTypeId' }, totalCount: { $ifNull: [ { $arrayElemAt: [ '$_associations._assocCount', 0 ] }, 0 ] }, 'targets': [] }")
            .ToListAsync();

        foreach (var multipleResult in result)
        {
            var aggregations = FinalizeResults(multipleResult.Targets);
            multipleResult.AggregationResult = aggregations.Item1;
            multipleResult.FieldAggregationResults = aggregations.Item2;
        }

        return new MultipleOriginResultSet<TTargetEntity>(result);
    }

    private async Task<IMultipleOriginResultSet<TTargetEntity>> ExecuteQueryCore(IOctoSession session, int? skip,
        int? take)
    {
        // Field names for the association lookup
        var (innerLocalFieldRtIdName, innerLocalFieldCkIdName, connectToFieldName) = GetDirectionFieldNames();

        var connectToField = (FieldDefinition<RtAssociation, string>)connectToFieldName;

        var connectFromField = (FieldDefinition<RtEntity, string[]>)"_id";
        var @as = (FieldDefinition<BsonDocument, TTargetEntity[]>)"_associations";

        var allTargetCkIds = GetAllTargetCkIds();

        // Group target type graphs by their collection root
        var collectionGroups = _targetCkTypeGraphs
            .GroupBy(g => g.DefiningCollectionRootCkTypeId)
            .ToList();

        // Build the sub-pipeline using BsonDocument stages for flexibility with multiple lookups
        var pipelineStages = new List<BsonDocument>
        {
            // Match associations
            BuildAssociationMatchStage(innerLocalFieldCkIdName, allTargetCkIds)
        };

        // Pre-render field filters (used by both paths)
        var renderedFieldFilter = RenderTargetFieldFilter();

        var sortStage = GetSortStageBsonDocument();

        // Adaptive path selection (AB#4369): for sorted+limited navigations, origins with a huge
        // edge count are answered by the reversed, entity-driven query instead of materializing
        // their whole edge set. Origins below the threshold stay on the edge-driven path below.
        var edgeDrivenOriginIds = _rtIds.ToList();
        var reversedResults = new List<QueryMultipleResult<TTargetEntity>>();

        if (HasSortDefinitions && take.HasValue && _geospatialFilters.Count == 0 && collectionGroups.Count == 1)
        {
            var denseOrigins = new List<(OctoObjectId OriginRtId, long EdgeCount)>();
            var sparseOrigins = new List<OctoObjectId>();

            foreach (var originId in edgeDrivenOriginIds)
            {
                // Capped, index-only count: stops scanning at threshold+1. Deliberately without
                // the rtState filter — that would force a document fetch per index entry; the
                // count is a routing signal (and, for dense origins, the display totalCount,
                // where stray archived edges are an acceptable approximation).
                var edgeCountFilter = BuildEdgeCountFilter(originId, connectToFieldName,
                    innerLocalFieldCkIdName, allTargetCkIds);
                var cappedCount = await _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations
                    .GetTotalCountAsync(session, edgeCountFilter, ReverseNavigationOptions.EdgeThreshold + 1);

                if (cappedCount > ReverseNavigationOptions.EdgeThreshold)
                {
                    var totalEdgeCount = await _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations
                        .GetTotalCountAsync(session, edgeCountFilter);
                    denseOrigins.Add((originId, totalEdgeCount));
                }
                else
                {
                    sparseOrigins.Add(originId);
                }
            }

            if (denseOrigins.Count > 0)
            {
                var originCkTypeIds = await LoadOriginCkTypeIdsAsync(session,
                    denseOrigins.Select(d => d.OriginRtId).ToList());

                foreach (var (originId, edgeCount) in denseOrigins)
                {
                    if (!originCkTypeIds.TryGetValue(originId, out var originEntityCkTypeId))
                    {
                        // Origin entity no longer exists — the edge-driven path would not have
                        // produced a result row for it either.
                        continue;
                    }

                    var (targets, filteredTotal) = await ExecuteReversedNavigationAsync(session, originId,
                        collectionGroups[0].ToList(), renderedFieldFilter, sortStage!, skip, take.Value,
                        innerLocalFieldRtIdName, connectToFieldName);

                    reversedResults.Add(new QueryMultipleResult<TTargetEntity>
                    {
                        Id = new RtEntityId(originEntityCkTypeId, originId),
                        // AB#6249: with a field filter the edge count is not the size of the result set.
                        TotalCount = filteredTotal ?? edgeCount,
                        Targets = targets
                    });
                }

                edgeDrivenOriginIds = sparseOrigins;

                if (edgeDrivenOriginIds.Count == 0)
                {
                    foreach (var reversedResult in reversedResults)
                    {
                        var reversedAggregations = FinalizeResults(reversedResult.Targets);
                        reversedResult.AggregationResult = reversedAggregations.Item1;
                        reversedResult.FieldAggregationResults = reversedAggregations.Item2;
                    }

                    return new MultipleOriginResultSet<TTargetEntity>(reversedResults);
                }
            }
        }

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

                    // AB#6249: the association count (assocCount) says nothing about how many targets
                    // survive the field filter. Count the filtered set before sort+limit; the count
                    // travels on the targets as _assocCount, exactly like the unfiltered path.
                    innerPipeline.Add(new BsonDocument("$setWindowFields", new BsonDocument("output",
                        new BsonDocument("_assocCount", new BsonDocument
                        {
                            { "$sum", 1 },
                            { "window", new BsonDocument("documents", new BsonArray { "unbounded", "unbounded" }) }
                        }))));
                }

                // Sort inside the lookup
                if (sortStage != null)
                {
                    innerPipeline.Add(sortStage);
                }

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
            }
            else
            {
                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("target", "$target0")));
            }

            // Embed the total into each target element for totalCount. Without a field filter this is
            // the association count already computed by $group (avoids expensive $setWindowFields over
            // all target entities). With a field filter the inner pipeline has already counted the
            // filtered set per collection group (AB#6249); several groups are summed here.
            if (renderedFieldFilter == null)
            {
                pipelineStages.Add(BuildEmbedAssocCountStage("$assocCount"));
            }
            else if (collectionGroups.Count > 1)
            {
                var summands = new BsonArray();
                for (var i = 0; i < collectionGroups.Count; i++)
                {
                    summands.Add(new BsonDocument("$ifNull",
                        new BsonArray { new BsonDocument("$arrayElemAt", new BsonArray { $"$target{i}._assocCount", 0 }), 0 }));
                }

                pipelineStages.Add(new BsonDocument("$addFields",
                    new BsonDocument("_filteredTotal", new BsonDocument("$add", summands))));
                pipelineStages.Add(BuildEmbedAssocCountStage("$_filteredTotal"));
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
            .Match(Builders<RtEntity>.Filter.In(x => x.RtId, edgeDrivenOriginIds))
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
        result.AddRange(reversedResults);

        foreach (var multipleResult in result)
        {
            var targetEntities = FinalizeResults(multipleResult.Targets);
            multipleResult.AggregationResult = targetEntities.Item1;
            multipleResult.FieldAggregationResults = targetEntities.Item2;
        }

        return new MultipleOriginResultSet<TTargetEntity>(result);
    }

    private static BsonDocument BuildEmbedAssocCountStage(string countExpression)
    {
        return new BsonDocument("$addFields", new BsonDocument("target",
            new BsonDocument("$map", new BsonDocument
            {
                { "input", "$target" },
                { "as", "t" },
                {
                    "in", new BsonDocument("$mergeObjects", new BsonArray
                    {
                        "$$t",
                        new BsonDocument("_assocCount", countExpression)
                    })
                }
            })));
    }

    private FilterDefinition<RtAssociation> BuildEdgeCountFilter(OctoObjectId originRtId, string connectToFieldName,
        string innerLocalFieldCkIdName, IReadOnlyList<RtCkId<CkTypeId>> allTargetCkIds)
    {
        return Builders<RtAssociation>.Filter.And(
            Builders<RtAssociation>.Filter.Eq(connectToFieldName, originRtId),
            Builders<RtAssociation>.Filter.Eq("associationRoleId", _roleId),
            Builders<RtAssociation>.Filter.In(innerLocalFieldCkIdName, allTargetCkIds));
    }

    private async Task<Dictionary<OctoObjectId, RtCkId<CkTypeId>>> LoadOriginCkTypeIdsAsync(IOctoSession session,
        IReadOnlyList<OctoObjectId> originRtIds)
    {
        var originEntities = await _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph)
            .Aggregate(session)
            .Match(Builders<RtEntity>.Filter.In(x => x.RtId, originRtIds))
            .ToListAsync();

        return originEntities
            .Where(e => e.CkTypeId != null)
            .ToDictionary(e => e.RtId, e => e.CkTypeId!);
    }

    /// <summary>
    ///     Entity-driven navigation for dense origins (AB#4369): walks the target collection in
    ///     sort order (index-backed where the sort attribute is indexed) and probes edge
    ///     existence per candidate via an indexed $lookup, stopping as soon as the requested
    ///     page is filled. Cost scales with the page size instead of the origin's edge count.
    /// </summary>
    private async Task<(List<TTargetEntity> Targets, long? FilteredTotal)> ExecuteReversedNavigationAsync(IOctoSession session,
        OctoObjectId originRtId, IReadOnlyList<CkTypeGraph> groupGraphs, BsonDocument? renderedFieldFilter,
        BsonDocument sortStage, int? skip, int take, string innerLocalFieldRtIdName, string connectToFieldName)
    {
        var groupTargetCkIds = groupGraphs
            .SelectMany(graph => graph.GetAllDerivedTypes(true))
            .Select(t => t.ToRtCkId())
            .Distinct()
            .ToList();

        var entityRenderArgs = new RenderArgs<TTargetEntity>(
            BsonSerializer.SerializerRegistry.GetSerializer<TTargetEntity>(),
            BsonSerializer.SerializerRegistry);

        List<FilterDefinition<TTargetEntity>> entityFilters =
        [
            Builders<TTargetEntity>.Filter.In("ckTypeId", groupTargetCkIds)
        ];

        if (!_includeArchivedEntities)
        {
            entityFilters.Add(Builders<TTargetEntity>.Filter.Ne(e => e.RtState, RtState.Archived));
        }

        var stages = new List<BsonDocument>
        {
            new("$match", Builders<TTargetEntity>.Filter.And(entityFilters).Render(entityRenderArgs))
        };

        if (renderedFieldFilter != null)
        {
            stages.Add(new BsonDocument("$match", renderedFieldFilter));
        }

        stages.Add(sortStage);

        // Edge-existence probe. The candidate entity sits on the navigation's far side, so for
        // an Inbound navigation the edge points FROM the candidate TO the origin (probe by
        // originRtId, filter targetRtId == origin) and vice versa. Both directions have an
        // RtAssociation index led by the probe field (originRtId / targetRtId), so the concise
        // localField/foreignField join is index-backed and each probe touches only the
        // candidate's few edges of this role.
        var associationRenderArgs = new RenderArgs<RtAssociation>(
            BsonSerializer.SerializerRegistry.GetSerializer<RtAssociation>(),
            BsonSerializer.SerializerRegistry);

        List<FilterDefinition<RtAssociation>> probeFilters =
        [
            Builders<RtAssociation>.Filter.Eq("associationRoleId", _roleId),
            Builders<RtAssociation>.Filter.Eq(connectToFieldName, originRtId)
        ];

        if (!_includeArchivedEntities)
        {
            probeFilters.Add(Builders<RtAssociation>.Filter.Ne(a => a.RtState, RtState.Archived));
        }

        var probePipeline = new BsonArray
        {
            new BsonDocument("$match", Builders<RtAssociation>.Filter.And(probeFilters).Render(associationRenderArgs)),
            new BsonDocument("$limit", 1),
            new BsonDocument("$project", new BsonDocument("_id", 1))
        };

        stages.Add(new BsonDocument("$lookup", new BsonDocument
        {
            { "from", _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.CollectionName },
            { "localField", "_id" },
            { "foreignField", innerLocalFieldRtIdName },
            { "pipeline", probePipeline },
            { "as", "_edgeProbe" }
        }));

        stages.Add(new BsonDocument("$match", new BsonDocument("_edgeProbe.0", new BsonDocument("$exists", true))));
        stages.Add(new BsonDocument("$project", new BsonDocument("_edgeProbe", 0)));

        // Everything above is the full filtered+connected set; the count pipeline reuses it.
        var countStages = new List<BsonDocument>(stages);
        countStages.RemoveAt(countStages.IndexOf(sortStage));

        if (skip is > 0)
        {
            stages.Add(new BsonDocument("$skip", skip.Value));
        }

        stages.Add(new BsonDocument("$limit", take));

        var fluent = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TTargetEntity>(groupGraphs[0])
            .Aggregate(session);
        foreach (var stage in stages)
        {
            fluent = fluent.AppendStage(new BsonDocumentPipelineStageDefinition<TTargetEntity, TTargetEntity>(stage));
        }

        var targets = await fluent.ToListAsync();

        if (renderedFieldFilter == null)
        {
            return (targets, null);
        }

        // AB#6249: with a field filter the edge count is not the result size. A page that is not
        // full already tells the exact total; only a full page needs the (sort-free) count pass.
        var offset = skip ?? 0;
        if (targets.Count < take)
        {
            return (targets, offset + targets.Count);
        }

        var countFluent = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TTargetEntity>(groupGraphs[0])
            .Aggregate(session);
        foreach (var stage in countStages)
        {
            countFluent = countFluent.AppendStage(
                new BsonDocumentPipelineStageDefinition<TTargetEntity, TTargetEntity>(stage));
        }

        var counted = await countFluent
            .Group(new BsonDocument { { "_id", BsonNull.Value }, { "n", new BsonDocument("$sum", 1) } })
            .FirstOrDefaultAsync();
        return (targets, counted == null ? 0 : counted["n"].ToInt64());
    }


    private SecretAttributeReadNormalizer? _secretAttributeReadNormalizer;

    protected override void OnResultsMaterialized(IEnumerable<TTargetEntity> resultList)
    {
        // AB#5533: legacy strings in Secret slots are handed out as RtSecretValue.LegacyPlaintext.
        // One normaliser per query: a multiple-origin result calls this once per origin, and the
        // per-type Secret shape is then computed once instead of once per origin.
        _secretAttributeReadNormalizer ??= new SecretAttributeReadNormalizer(_ckCacheService, _tenantId);
        _secretAttributeReadNormalizer.Normalize(resultList);
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
