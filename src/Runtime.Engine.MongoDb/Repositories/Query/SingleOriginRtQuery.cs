using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class SingleOriginRtQuery<TEntity> : SingleOriginQuery<OctoObjectId, TEntity> where TEntity : RtEntity, new()
{
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly CkTypeGraph _ckTypeGraph;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly bool _includeDeletedEntities;
    private readonly List<IPipelineStageDefinition> _geospatialFilters;
    private readonly List<IPipelineStageDefinition> _associationStageDefinitions;
    private readonly List<IPipelineStageDefinition> _enrichmentStageDefinitions;

    internal SingleOriginRtQuery(IMetricsContext metricsContext, ICkCacheService ckCacheService, string tenantId,
        CkTypeGraph ckTypeGraph,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, string language, bool includeDeletedEntities)
        : base(metricsContext, mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph),
            new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph), language)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _ckTypeGraph = ckTypeGraph;
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _includeDeletedEntities = includeDeletedEntities;
        _geospatialFilters = new List<IPipelineStageDefinition>();
        _associationStageDefinitions = new List<IPipelineStageDefinition>();
        _enrichmentStageDefinitions = new List<IPipelineStageDefinition>();
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

                _geospatialFilters.Add(OctoPipelineStageBuilder.GeoNear<TEntity, GeoJsonCoordinates>(
                    resolvedAttributeName, point, nearGeospatialFilter.MinDistance, nearGeospatialFilter.MaxDistance));
            }
        }
    }


    /// <summary>
    /// Adds navigation properties to the query.
    /// In <see cref="NavigationFilterMode.Filter"/> mode (default), navigation stages are added pre-pagination,
    /// filtering out entities without associations.
    /// In <see cref="NavigationFilterMode.Include"/> mode, navigation stages are deferred as enrichment stages
    /// that run post-pagination, improving performance for large result sets.
    /// The mode is resolved per pair: a pair that carries field-filter criteria (e.g. an association
    /// field filter such as <c>parent.type-&gt;rtId EQUALS x</c>) must filter pre-pagination even when
    /// the query-level mode is Include — the Include mode only exists so that pure column pairs
    /// (::exists/::totalCount cells, first-match value navigation) don't drop entities without
    /// associations; it must never disable an explicit filter.
    /// </summary>
    /// <param name="roleIdDirectionPairs">The navigation pairs to add.</param>
    /// <param name="navigationFilterMode">Controls whether entities without associations are filtered or included.</param>
    public void AddNavigationProperties(IEnumerable<NavigationPair> roleIdDirectionPairs,
        NavigationFilterMode navigationFilterMode = NavigationFilterMode.Filter)
    {
        foreach (var roleIdDirectionPair in roleIdDirectionPairs)
        {
            // Check if there's a real count filter (not the permissive default GreaterEqualThan 0)
            var hasRealCountFilter = roleIdDirectionPair.AssociationCountFilter != null &&
                !(roleIdDirectionPair.AssociationCountFilter.Operator == FieldFilterOperator.GreaterEqualThan &&
                  roleIdDirectionPair.AssociationCountFilter.ComparisonValue == 0);

            if (hasRealCountFilter)
            {
                // N:M association count filter: count associations and filter by count pre-pagination,
                // then enrich with full associations post-pagination for the cell value
                CreateAssociationCountNavigation(roleIdDirectionPair, _ckTypeGraph, _associationStageDefinitions);
                CreateInnerNavigation(roleIdDirectionPair, _ckTypeGraph, _enrichmentStageDefinitions, false);
            }
            else if (navigationFilterMode == NavigationFilterMode.Filter ||
                     CarriesFieldFilters(roleIdDirectionPair))
            {
                // Two-phase FILTER: lightweight existence check pre-pagination (for filtering + count),
                // full data enrichment post-pagination (only on the paginated subset).
                CreateExistenceCheckNavigation(roleIdDirectionPair, _ckTypeGraph, _associationStageDefinitions);
                CreateInnerNavigation(roleIdDirectionPair, _ckTypeGraph, _enrichmentStageDefinitions, false);
            }
            else
            {
                // Include mode: full enrichment post-pagination only (no filtering)
                CreateInnerNavigation(roleIdDirectionPair, _ckTypeGraph, _enrichmentStageDefinitions, false);
            }
        }
    }

    /// <summary>
    /// True when the navigation pair (or any inner pair on a deeper path segment) carries
    /// explicit field-filter criteria that must narrow the origin result set.
    /// </summary>
    private static bool CarriesFieldFilters(NavigationPair pair)
    {
        return pair.FieldFilters is { Count: > 0 } ||
               pair.NestedFilters is { Count: > 0 } ||
               pair.InnerNavigationPairs.Any(CarriesFieldFilters);
    }

    /// <summary>
    /// Resolves the inbound association graph and the reached entity type for a navigation pair.
    /// With reached-type path semantics (AB#4323) the pair's TargetCkTypeId addresses the
    /// association's ORIGIN side (or a subtype of it) — exactly the entities the navigation loads,
    /// so the lookup narrows to that subtype's derived set. Legacy pairs carried the queried type
    /// itself (the In graph's declared TargetCkTypeId); those fall back to the whole origin-side
    /// hierarchy of the association.
    /// </summary>
    private (CkTypeAssociationGraph? Association, CkTypeGraph ReachedCkTypeGraph) ResolveInboundNavigation(
        CkTypeGraph originCkTypeGraph, NavigationPair roleIdDirectionPair, List<CkId<CkTypeId>> baseCkTypeIds,
        CkTypeGraph targetCkTypeGraph)
    {
        var association = originCkTypeGraph.Associations.In.All.FirstOrDefault(a =>
            a.CkRoleId.Equals(roleIdDirectionPair.CkRoleId) && baseCkTypeIds.Contains(a.OriginCkTypeId));
        if (association != null)
        {
            return (association, targetCkTypeGraph);
        }

        association = originCkTypeGraph.Associations.In.All.FirstOrDefault(a =>
            a.CkRoleId.Equals(roleIdDirectionPair.CkRoleId) && baseCkTypeIds.Contains(a.TargetCkTypeId));
        return association == null
            ? (null, targetCkTypeGraph)
            : (association, _ckCacheService.GetCkType(_tenantId, association.OriginCkTypeId));
    }

    private void CreateInnerNavigation(NavigationPair roleIdDirectionPair, CkTypeGraph originCkTypeGraph,
        List<IPipelineStageDefinition> stageDefinitions, bool filterEntitiesWithoutAssociations = true)
    {
        var targetCkTypeGraph = _ckCacheService.GetRtCkType(_tenantId, roleIdDirectionPair.TargetCkTypeId);

        // We need to have a list of all ck type ids we should handle as a candidate for the association target ck type id.
        var baseCkTypeIds = targetCkTypeGraph.BaseTypes.Select(b => b.BaseCkTypeId).ToList();
        baseCkTypeIds.Add(targetCkTypeGraph.CkTypeId);

        var innerLocalFieldRtId = (FieldDefinition<RtAssociation, string>)"originRtId";
        var foreignFieldRtId = (FieldDefinition<RtAssociation>)"targetRtId";
        // We ensure that the association role exists.
        // Because navigation properties are centralized in the definition, all
        // associations with the same role id have the same navigation property name.
        CkTypeAssociationGraph? association;

        // For the association lookup filter, we need to match the correct CkTypeId field:
        // Inbound: join on targetRtId, filter by originCkTypeId (the type that created the association)
        // Outbound: join on originRtId, filter by targetCkTypeId
        FieldDefinition<RtAssociation, RtCkId<CkTypeId>> ckTypeIdFilterField;
        IEnumerable<RtCkId<CkTypeId>> ckTypeIdsToMatch;

        switch (roleIdDirectionPair.Direction)
        {
            case GraphDirections.Outbound:
                innerLocalFieldRtId = "targetRtId";
                foreignFieldRtId = "originRtId";
                association = originCkTypeGraph.Associations.Out.All.FirstOrDefault(a =>
                    baseCkTypeIds.Contains(a.TargetCkTypeId) &&
                    a.CkRoleId.Equals(roleIdDirectionPair.CkRoleId));
                ckTypeIdFilterField = "targetCkTypeId";
                ckTypeIdsToMatch = targetCkTypeGraph.GetAllDerivedTypes(true).Select(e => e.ToRtCkId());
                break;
            case GraphDirections.Inbound:
                ckTypeIdFilterField = "originCkTypeId";
                var (inboundAssociation, reachedCkTypeGraph) = ResolveInboundNavigation(originCkTypeGraph,
                    roleIdDirectionPair, baseCkTypeIds, targetCkTypeGraph);
                association = inboundAssociation;
                ckTypeIdsToMatch = reachedCkTypeGraph.GetAllDerivedTypes(true).Select(e => e.ToRtCkId());
                // Use the reached type's graph for the inner entity lookup collection
                targetCkTypeGraph = reachedCkTypeGraph;
                break;
            default:
                throw OperationFailedException.GraphDirectionUnsupported(roleIdDirectionPair.Direction);
        }

        if (association == null)
        {
            throw OperationFailedException.AssociationNotFound(roleIdDirectionPair.CkRoleId,
                roleIdDirectionPair.TargetCkTypeId);
        }

        var innerLookupPipelineStages = new List<IPipelineStageDefinition>();

        var targetCkTypeFilter = new List<FilterDefinition<RtEntityGraphItem>>();
        var fieldFilterResolver =
            new RtEntityGraphItemFieldFilterResolver(_ckCacheService, _tenantId, targetCkTypeGraph);
        fieldFilterResolver.AddFieldFilterCriteria(roleIdDirectionPair);
        targetCkTypeFilter.AddRange(fieldFilterResolver.FilterDefinitions);
        if (targetCkTypeFilter.Any())
        {
            var filterDefinitions = targetCkTypeFilter.Count == 1
                ? targetCkTypeFilter.First()
                : Builders<RtEntityGraphItem>.Filter.And(targetCkTypeFilter);

            if (filterDefinitions != null)
            {
                innerLookupPipelineStages.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
            }
        }

        foreach (NavigationPair innerNavigationPair in roleIdDirectionPair.InnerNavigationPairs)
        {
            CreateInnerNavigation(innerNavigationPair, targetCkTypeGraph, innerLookupPipelineStages);
        }

        var innerLookupPipeline =
            PipelineDefinition<TEntity, TEntity>.Create(innerLookupPipelineStages);

        var lookupPipelineStages = new List<IPipelineStageDefinition>
        {
            PipelineStageDefinitionBuilder.Match(
                Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(f => f.AssociationRoleId, roleIdDirectionPair.CkRoleId),
                    Builders<RtAssociation>.Filter.In(ckTypeIdFilterField, ckTypeIdsToMatch)
                )
            ),
            OctoPipelineStageBuilder
                .Lookup<RtAssociation, TEntity, TEntity, IEnumerable<TEntity>,
                    RtEntityGraphItem>(
                    _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(targetCkTypeGraph)
                        .GetMongoCollection(),
                    innerLocalFieldRtId,
                    "_id",
                    (FieldDefinition<RtEntityGraphItem, IEnumerable<TEntity>>)"targets",
                    innerLookupPipeline),
            PipelineStageDefinitionBuilder.Match(
                Builders<RtEntityGraphItem>.Filter.SizeGt("targets", 0)
            ),
            PipelineStageDefinitionBuilder.Project<TEntity, RtAssociationWithEntities>(
                new BsonDocument { { "_id", 1 }, { "rtAssociationRoleId", "$associationRoleId" }, { "attributes", 1 }, { "targets", 1 } }),
        };

        var fieldTargetRtCkTypeId =
            Tuple.Create<FieldDefinition<RtAssociationWithEntities, RtAssociationWithEntities>,
                AggregateExpressionDefinition<RtAssociationWithEntities, RtAssociationWithEntities>>(
                "targetRtCkTypeId",
                OctoBuilder<RtAssociationWithEntities, RtAssociationWithEntities>.AggregateOperators.String(
                    roleIdDirectionPair.TargetCkTypeId
                        .SemanticVersionedFullName));
        var fieldNavigationPropertyName =
            Tuple.Create<FieldDefinition<RtAssociationWithEntities, RtAssociationWithEntities>,
                AggregateExpressionDefinition<RtAssociationWithEntities, RtAssociationWithEntities>>(
                "navigationPropertyName",
                OctoBuilder<RtAssociationWithEntities, RtAssociationWithEntities>.AggregateOperators.String(
                    association.NavigationPropertyName));

        lookupPipelineStages.Add(
            OctoPipelineStageBuilder.AddFields<RtAssociationWithEntities, RtAssociationWithEntities>(
                OctoBuilder<RtAssociationWithEntities, RtAssociationWithEntities>.Fields.SetMultiple(
                    fieldTargetRtCkTypeId, fieldNavigationPropertyName)));

        var lookupPipeline =
            PipelineDefinition<RtAssociation, RtAssociationWithEntities>.Create(lookupPipelineStages);

        stageDefinitions.Add(
            OctoPipelineStageBuilder
                .Lookup<TEntity, RtAssociation, RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>,
                    RtAssociationWithEntities>(
                    _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                    "_id",
                    foreignFieldRtId,
                    (FieldDefinition<RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>>)
                    "__associations",
                    lookupPipeline));

        if (filterEntitiesWithoutAssociations)
        {
            stageDefinitions.Add(PipelineStageDefinitionBuilder.Match(
                Builders<RtAssociationWithEntities>.Filter.SizeGt("__associations", 0)
            ));
        }

        stageDefinitions.Add(PipelineStageDefinitionBuilder.Project(
            OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.Fields(
            [
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("_id",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("ckTypeId",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("attributes",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtChangedDateTime",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtCreationDateTime",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtVersion",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtWellKnownName",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("_associations",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.ConcatArrays(
                        OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.IfNull(
                            OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Field(
                                "$_associations"),
                            OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.EmptyArray()),
                        OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Field("$__associations")
                    )),
            ])));
    }

    /// <summary>
    /// Creates lightweight existence-check navigation stages for the two-phase FILTER optimization.
    /// These stages only verify whether associations exist (with $limit:1 in the lookup pipeline)
    /// without resolving full navigation data. This runs pre-pagination for filtering and counting.
    /// The full data enrichment runs post-pagination via <see cref="CreateInnerNavigation"/>.
    /// </summary>
    private void CreateExistenceCheckNavigation(NavigationPair roleIdDirectionPair, CkTypeGraph originCkTypeGraph,
        List<IPipelineStageDefinition> stageDefinitions)
    {
        var targetCkTypeGraph = _ckCacheService.GetRtCkType(_tenantId, roleIdDirectionPair.TargetCkTypeId);

        var baseCkTypeIds = targetCkTypeGraph.BaseTypes.Select(b => b.BaseCkTypeId).ToList();
        baseCkTypeIds.Add(targetCkTypeGraph.CkTypeId);

        var innerLocalFieldRtId = (FieldDefinition<RtAssociation, string>)"originRtId";
        var foreignFieldRtId = (FieldDefinition<RtAssociation>)"targetRtId";
        CkTypeAssociationGraph? association;

        FieldDefinition<RtAssociation, RtCkId<CkTypeId>> ckTypeIdFilterField;
        IEnumerable<RtCkId<CkTypeId>> ckTypeIdsToMatch;

        switch (roleIdDirectionPair.Direction)
        {
            case GraphDirections.Outbound:
                innerLocalFieldRtId = "targetRtId";
                foreignFieldRtId = "originRtId";
                association = originCkTypeGraph.Associations.Out.All.FirstOrDefault(a =>
                    baseCkTypeIds.Contains(a.TargetCkTypeId) &&
                    a.CkRoleId.Equals(roleIdDirectionPair.CkRoleId));
                ckTypeIdFilterField = "targetCkTypeId";
                ckTypeIdsToMatch = targetCkTypeGraph.GetAllDerivedTypes(true).Select(e => e.ToRtCkId());
                break;
            case GraphDirections.Inbound:
                ckTypeIdFilterField = "originCkTypeId";
                var (inboundAssociation, reachedCkTypeGraph) = ResolveInboundNavigation(originCkTypeGraph,
                    roleIdDirectionPair, baseCkTypeIds, targetCkTypeGraph);
                association = inboundAssociation;
                ckTypeIdsToMatch = reachedCkTypeGraph.GetAllDerivedTypes(true).Select(e => e.ToRtCkId());
                targetCkTypeGraph = reachedCkTypeGraph;
                break;
            default:
                throw OperationFailedException.GraphDirectionUnsupported(roleIdDirectionPair.Direction);
        }

        if (association == null)
        {
            throw OperationFailedException.AssociationNotFound(roleIdDirectionPair.CkRoleId,
                roleIdDirectionPair.TargetCkTypeId);
        }

        // Build the same inner lookup pipeline for field filters (needed for correctness)
        var innerLookupPipelineStages = new List<IPipelineStageDefinition>();

        var targetCkTypeFilter = new List<FilterDefinition<RtEntityGraphItem>>();
        var fieldFilterResolver =
            new RtEntityGraphItemFieldFilterResolver(_ckCacheService, _tenantId, targetCkTypeGraph);
        fieldFilterResolver.AddFieldFilterCriteria(roleIdDirectionPair);
        targetCkTypeFilter.AddRange(fieldFilterResolver.FilterDefinitions);
        if (targetCkTypeFilter.Any())
        {
            var filterDefinitions = targetCkTypeFilter.Count == 1
                ? targetCkTypeFilter.First()
                : Builders<RtEntityGraphItem>.Filter.And(targetCkTypeFilter);

            if (filterDefinitions != null)
            {
                innerLookupPipelineStages.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
            }
        }

        // Include nested navigation for correctness (target entities may be filtered by nested nav)
        foreach (NavigationPair innerNavigationPair in roleIdDirectionPair.InnerNavigationPairs)
        {
            CreateInnerNavigation(innerNavigationPair, targetCkTypeGraph, innerLookupPipelineStages);
        }

        var innerLookupPipeline =
            PipelineDefinition<TEntity, TEntity>.Create(innerLookupPipelineStages);

        // Simplified lookup pipeline: match + inner lookup + match targets + $limit:1 + minimal $project
        // Skips full $addFields (metadata not needed for existence check)
        var lookupPipelineStages = new List<IPipelineStageDefinition>
        {
            PipelineStageDefinitionBuilder.Match(
                Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(f => f.AssociationRoleId, roleIdDirectionPair.CkRoleId),
                    Builders<RtAssociation>.Filter.In(ckTypeIdFilterField, ckTypeIdsToMatch)
                )
            ),
            OctoPipelineStageBuilder
                .Lookup<RtAssociation, TEntity, TEntity, IEnumerable<TEntity>,
                    RtEntityGraphItem>(
                    _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(targetCkTypeGraph)
                        .GetMongoCollection(),
                    innerLocalFieldRtId,
                    "_id",
                    (FieldDefinition<RtEntityGraphItem, IEnumerable<TEntity>>)"targets",
                    innerLookupPipeline),
            PipelineStageDefinitionBuilder.Match(
                Builders<RtEntityGraphItem>.Filter.SizeGt("targets", 0)
            ),
            // Stop after finding the first valid association (existence check optimization)
            PipelineStageDefinitionBuilder.Limit<RtEntityGraphItem>(1),
            // Minimal projection to match expected output type
            PipelineStageDefinitionBuilder.Project<RtEntityGraphItem, RtAssociationWithEntities>(
                new BsonDocument { { "_id", 1 } }),
        };

        var lookupPipeline =
            PipelineDefinition<RtAssociation, RtAssociationWithEntities>.Create(lookupPipelineStages);

        // Outer lookup: same as full version
        stageDefinitions.Add(
            OctoPipelineStageBuilder
                .Lookup<TEntity, RtAssociation, RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>,
                    RtAssociationWithEntities>(
                    _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                    "_id",
                    foreignFieldRtId,
                    (FieldDefinition<RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>>)
                    "__associations",
                    lookupPipeline));

        // Filter entities without associations
        stageDefinitions.Add(PipelineStageDefinitionBuilder.Match(
            Builders<RtAssociationWithEntities>.Filter.SizeGt("__associations", 0)
        ));

        // Cleanup: project to keep entity fields, drop __associations
        stageDefinitions.Add(PipelineStageDefinitionBuilder.Project(
            OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.Fields(
            [
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("_id",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("ckTypeId",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("attributes",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtChangedDateTime",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtCreationDateTime",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtVersion",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtWellKnownName",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
            ])));
    }

    /// <summary>
    /// Creates association count navigation stages for N:M association meta queries.
    /// Counts the number of matching associations and filters entities based on the count.
    /// </summary>
    private void CreateAssociationCountNavigation(NavigationPair roleIdDirectionPair, CkTypeGraph originCkTypeGraph,
        List<IPipelineStageDefinition> stageDefinitions)
    {
        var targetCkTypeGraph = _ckCacheService.GetRtCkType(_tenantId, roleIdDirectionPair.TargetCkTypeId);

        var baseCkTypeIds = targetCkTypeGraph.BaseTypes.Select(b => b.BaseCkTypeId).ToList();
        baseCkTypeIds.Add(targetCkTypeGraph.CkTypeId);

        var foreignFieldRtId = (FieldDefinition<RtAssociation>)"targetRtId";
        CkTypeAssociationGraph? association;

        // For the count filter, we need to match the correct CkTypeId field in the association document.
        // Inbound: we join on targetRtId, filter by originCkTypeId (the type that created the association)
        // Outbound: we join on originRtId, filter by targetCkTypeId
        FieldDefinition<RtAssociation, RtCkId<CkTypeId>> ckTypeIdFilterField;
        IEnumerable<RtCkId<CkTypeId>> ckTypeIdsToMatch;

        switch (roleIdDirectionPair.Direction)
        {
            case GraphDirections.Outbound:
                foreignFieldRtId = "originRtId";
                association = originCkTypeGraph.Associations.Out.All.FirstOrDefault(a =>
                    baseCkTypeIds.Contains(a.TargetCkTypeId) &&
                    a.CkRoleId.Equals(roleIdDirectionPair.CkRoleId));
                // Outbound: filter by targetCkTypeId in association = the target type
                ckTypeIdFilterField = "targetCkTypeId";
                ckTypeIdsToMatch = targetCkTypeGraph.GetAllDerivedTypes(true).Select(e => e.ToRtCkId());
                break;
            case GraphDirections.Inbound:
                // Inbound: filter by originCkTypeId in association = the origin type (who created the association)
                ckTypeIdFilterField = "originCkTypeId";
                var (inboundAssociation, reachedCkTypeGraph) = ResolveInboundNavigation(originCkTypeGraph,
                    roleIdDirectionPair, baseCkTypeIds, targetCkTypeGraph);
                association = inboundAssociation;
                ckTypeIdsToMatch = reachedCkTypeGraph.GetAllDerivedTypes(true).Select(e => e.ToRtCkId());
                break;
            default:
                throw OperationFailedException.GraphDirectionUnsupported(roleIdDirectionPair.Direction);
        }

        if (association == null)
        {
            throw OperationFailedException.AssociationNotFound(roleIdDirectionPair.CkRoleId,
                roleIdDirectionPair.TargetCkTypeId);
        }

        var countFilter = roleIdDirectionPair.AssociationCountFilter!;

        // Lookup pipeline: match associations by role id and ck type, then count
        var lookupPipelineStages = new List<IPipelineStageDefinition>
        {
            PipelineStageDefinitionBuilder.Match(
                Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(f => f.AssociationRoleId, roleIdDirectionPair.CkRoleId),
                    Builders<RtAssociation>.Filter.In(ckTypeIdFilterField, ckTypeIdsToMatch)
                )
            ),
            // Minimal projection — we only need the count
            PipelineStageDefinitionBuilder.Project<RtAssociation, RtAssociationWithEntities>(
                new BsonDocument { { "_id", 1 } }),
        };

        var lookupPipeline =
            PipelineDefinition<RtAssociation, RtAssociationWithEntities>.Create(lookupPipelineStages);

        // Outer lookup
        stageDefinitions.Add(
            OctoPipelineStageBuilder
                .Lookup<TEntity, RtAssociation, RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>,
                    RtAssociationWithEntities>(
                    _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                    "_id",
                    foreignFieldRtId,
                    (FieldDefinition<RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>>)
                    "__assocCount",
                    lookupPipeline));

        // AddFields: compute count from __assocCount array size
        stageDefinitions.Add(
            new BsonDocumentPipelineStageDefinition<RtAssociationWithEntities, RtAssociationWithEntities>(
                new BsonDocument("$addFields", new BsonDocument("__assocCountVal",
                    new BsonDocument("$size", "$__assocCount")))));

        // Match: filter by count using the operator from AssociationCountFilter
        var compVal = new BsonInt32(countFilter.ComparisonValue);
        BsonValue matchValue = countFilter.Operator switch
        {
            FieldFilterOperator.Equals => compVal,
            FieldFilterOperator.NotEquals => new BsonDocument("$ne", compVal),
            FieldFilterOperator.GreaterThan => new BsonDocument("$gt", compVal),
            FieldFilterOperator.GreaterEqualThan => new BsonDocument("$gte", compVal),
            FieldFilterOperator.LessThan => new BsonDocument("$lt", compVal),
            FieldFilterOperator.LessEqualThan => new BsonDocument("$lte", compVal),
            _ => throw OperationFailedException.OperatorNotSupported(countFilter.Operator)
        };

        stageDefinitions.Add(
            new BsonDocumentPipelineStageDefinition<RtAssociationWithEntities, RtAssociationWithEntities>(
                new BsonDocument("$match", new BsonDocument("__assocCountVal", matchValue))));

        // Cleanup: remove temporary fields, keep entity fields
        stageDefinitions.Add(PipelineStageDefinitionBuilder.Project(
            OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.Fields(
            [
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("_id",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("ckTypeId",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("attributes",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtChangedDateTime",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtCreationDateTime",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtVersion",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
                OctoBuilder<RtAssociationWithEntities, TEntity>.Projection.SingleField("rtWellKnownName",
                    OctoBuilder<RtAssociationWithEntities, TEntity>.AggregateOperators.Int32(1)),
            ])));
    }

    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);

        // Add filter for ck type and derived ones
        var ckTypeIds = _ckTypeGraph.GetAllDerivedTypes(true).Select(t => t.ToRtCkId());
        filters.Add(Builders<TEntity>.Filter.In(f => f.CkTypeId, ckTypeIds));

        // Ensure that deleted entities are not added to the result if defined.
        if (!_includeDeletedEntities)
        {
            filters.Add(Builders<TEntity>.Filter.Ne(ckType => ckType.RtState, RtState.Archived));
        }
    }

    protected override void AddPreStagesToPipelines(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        _geospatialFilters.ForEach(pipelineStageDefinitions.Add);

        base.AddPostStagesToPipeline(pipelineStageDefinitions);
    }

    protected override void AddPostStagesToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        _associationStageDefinitions.ForEach(pipelineStageDefinitions.Add);

        base.AddPostStagesToPipeline(pipelineStageDefinitions);
    }

    protected override void AddPrePaginationPostStagesToPipeline(
        IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        _associationStageDefinitions.ForEach(pipelineStageDefinitions.Add);
    }

    protected override FilterDefinition<TEntity> CreateIdInFilter(IEnumerable<TEntity> entities)
    {
        var ids = entities.Cast<RtEntity>().Select(e => e.RtId).ToList();
        return Builders<TEntity>.Filter.In("_id", ids);
    }

    internal override IReadOnlyList<IPipelineStageDefinition> GetEnrichmentStageDefinitions()
        => _enrichmentStageDefinitions;

    protected override (AggregationResult?, IEnumerable<FieldAggregationResult>?) CalculateAggregations(
        IEnumerable<TEntity> resultList)
    {
        if (ResultAggregation == null && FieldAggregation == null)
        {
            return (null, null);
        }

        var statisticFunctions =
            new RtStatisticFunctions<TEntity>(_ckCacheService, _tenantId, ResultAggregation, FieldAggregation);
        IEnumerable<TEntity> targetEntities = resultList as TEntity[] ?? resultList.ToArray();
        var fieldAggregationResults = statisticFunctions.CalculateFieldAggregation(targetEntities);
        var resultAggregation = statisticFunctions.CalculateResultAggregation(targetEntities);
        return (resultAggregation, fieldAggregationResults);
    }
}
