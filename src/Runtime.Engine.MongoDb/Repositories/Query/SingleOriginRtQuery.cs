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
    private readonly List<IPipelineStageDefinition> _geospatialFilters;
    private readonly List<IPipelineStageDefinition> _associationStageDefinitions;

    internal SingleOriginRtQuery(IMetricsContext metricsContext, ICkCacheService ckCacheService, string tenantId,
        CkTypeGraph ckTypeGraph,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, string language)
        : base(metricsContext, mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph),
            new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph), language)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _ckTypeGraph = ckTypeGraph;
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _geospatialFilters = new List<IPipelineStageDefinition>();
        _associationStageDefinitions = new List<IPipelineStageDefinition>();
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
            var resolvedAttributeName = FieldFilterResolver.ResolveAttributePath(geospatialFilter.AttributeName);
            if (string.IsNullOrWhiteSpace(resolvedAttributeName))
            {
                throw OperationFailedException.AttributeNameResolutionFailed(geospatialFilter.AttributeName);
            }

            if (geospatialFilter is NearGeospatialFilter nearGeospatialFilter)
            {
                GeoJsonPoint<GeoJsonCoordinates> point = nearGeospatialFilter.Point.ToGeoJsonPoint();

                _geospatialFilters.Add(OctoPipelineStageBuilder.GeoNear<TEntity, GeoJsonCoordinates>(
                    resolvedAttributeName, point, nearGeospatialFilter.MinDistance, nearGeospatialFilter.MaxDistance));
            }
        }
    }


    public void AddAssociations(IEnumerable<NavigationPair> roleIdDirectionPairs)
    {
        foreach (var roleIdDirectionPair in roleIdDirectionPairs)
        {
            var targetCkTypeGraph = _ckCacheService.GetCkType(_tenantId, roleIdDirectionPair.TargetCkTypeId);
            var targetCkTypeIds = targetCkTypeGraph.GetAllDerivedTypes(true);

            var innerLocalFieldRtId = (FieldDefinition<RtAssociation, string>)"originRtId";
            var foreignFieldRtId = (FieldDefinition<RtAssociation>)"targetRtId";
            var targetCkTypeIdField = (FieldDefinition<RtAssociation, CkId<CkTypeId>>)"originCkTypeId";
            // We ensure that the association role exists.
            // Because navigation properties are centralized in the definition, all
            // associations with the same role id have the same navigation property name.
            var association = _ckTypeGraph.Associations.In.All.FirstOrDefault(a =>
                a.TargetCkTypeId == roleIdDirectionPair.TargetCkTypeId && a.CkRoleId == roleIdDirectionPair.CkRoleId);

            switch (roleIdDirectionPair.Direction)
            {
                case GraphDirections.Outbound:
                    innerLocalFieldRtId = "targetRtId";
                    foreignFieldRtId = "originRtId";
                    association = _ckTypeGraph.Associations.Out.All.FirstOrDefault(a =>
                        a.TargetCkTypeId == roleIdDirectionPair.TargetCkTypeId &&
                        a.CkRoleId == roleIdDirectionPair.CkRoleId);
                    targetCkTypeIdField = (FieldDefinition<RtAssociation, CkId<CkTypeId>>)"targetCkTypeId";
                    break;
                case GraphDirections.Inbound:
                    break;
                default:
                    throw OperationFailedException.GraphDirectionUnsupported(roleIdDirectionPair.Direction);
            }

            if (association == null)
            {
                throw OperationFailedException.AssociationNotFound(roleIdDirectionPair.CkRoleId,
                    roleIdDirectionPair.TargetCkTypeId);
            }

            var lookupPipelineStages = new List<IPipelineStageDefinition>
            {
                PipelineStageDefinitionBuilder.Match(
                    Builders<RtAssociation>.Filter.And(
                        Builders<RtAssociation>.Filter.Eq(f => f.AssociationRoleId, roleIdDirectionPair.CkRoleId),
                        Builders<RtAssociation>.Filter.In(targetCkTypeIdField, targetCkTypeIds)
                    )
                ),
                PipelineStageDefinitionBuilder.Lookup<RtAssociation, RtEntity, RtEntity>(
                    _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(targetCkTypeGraph)
                        .GetMongoCollection(),
                    innerLocalFieldRtId,
                    "_id",
                    (FieldDefinition<RtEntity>)"targets"),
                PipelineStageDefinitionBuilder.Project<RtEntity, RtAssociationWithEntities>(
                    new BsonDocument
                    {
                        { "_id", 1 }, { "associationRoleId", 1 }, { "attributes", 1 }, { "targets", 1 }
                    }),
            };

            var fieldTargetCkTypeId =
                Tuple.Create<FieldDefinition<RtAssociationWithEntities, RtAssociationWithEntities>,
                    AggregateExpressionDefinition<RtAssociationWithEntities, RtAssociationWithEntities>>(
                    "targetCkTypeId",
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
                        fieldTargetCkTypeId, fieldNavigationPropertyName)));

            var lookupPipeline =
                PipelineDefinition<RtAssociation, RtAssociationWithEntities>.Create(lookupPipelineStages);

            _associationStageDefinitions.Add(
                OctoPipelineStageBuilder
                    .Lookup<TEntity, RtAssociation, RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>,
                        RtAssociationWithEntities>(
                        _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                        "_id",
                        foreignFieldRtId,
                        (FieldDefinition<RtAssociationWithEntities, IEnumerable<RtAssociationWithEntities>>)
                        "__associations",
                        lookupPipeline));

            _associationStageDefinitions.Add(PipelineStageDefinitionBuilder.Project(
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
    }

    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);

        // Add filter for ck type and derived ones
        var ckTypeIds = _ckTypeGraph.GetAllDerivedTypes(true);
        filters.Add(Builders<TEntity>.Filter.In(f => f.CkTypeId, ckTypeIds));
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

    protected override IEnumerable<GroupingResult>? CalculateGrouping(IEnumerable<TEntity> resultList)
    {
        if (GroupBy == null)
        {
            return null;
        }

        var statisticFunctions = new RtStatisticFunctions<TEntity>(_ckTypeGraph, GroupBy);
        return statisticFunctions.Calculate(resultList);
    }
}
