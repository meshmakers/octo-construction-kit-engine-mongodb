using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

// ReSharper disable once UnusedType.Global
internal class MultipleOriginIndirectAssociationsRtQuery : MultipleOriginIndirectAssociationsRtQuery<RtEntity>
{
    internal MultipleOriginIndirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkTypeGraph targetCkTypeGraph)
        : base(ckCacheService, tenantId, mongoDbRepositoryDataSource, language, rtIds, originCkTypeGraph, roleId,
            graphDirection,
            targetCkTypeGraph)
    {
    }
}

internal class MultipleOriginIndirectAssociationsRtQuery<TTargetEntity> : Query<TTargetEntity> where TTargetEntity : RtEntity, new()
{
    private readonly GraphDirections _graphDirection;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly CkTypeGraph _originCkTypeGraph;
    private readonly CkId<CkAssociationRoleId> _roleId;
    private readonly IEnumerable<OctoObjectId> _rtIds;
    private readonly CkTypeGraph _targetCkTypeGraph;
    private readonly List<IPipelineStageDefinition> _geospatialFilters;

    internal MultipleOriginIndirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkTypeGraph targetCkTypeGraph)
        : base(new RtEntityFieldFilterResolver<TTargetEntity>(ckCacheService, tenantId, targetCkTypeGraph), language)
    {
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _rtIds = rtIds;
        _originCkTypeGraph = originCkTypeGraph;
        _roleId = roleId;
        _graphDirection = graphDirection;
        _targetCkTypeGraph = targetCkTypeGraph;
        _geospatialFilters = new List<IPipelineStageDefinition>();
    }
    
    protected override void AddPreStagesToPipelines(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        _geospatialFilters.ForEach(pipelineStageDefinitions.Add);
        
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
            var resolvedAttributeName = FieldFilterResolver.ResolveAttributePath(geospatialFilter.AttributeName);
            if (string.IsNullOrWhiteSpace(resolvedAttributeName))
            {
                throw OperationFailedException.AttributeNameResolutionFailed(geospatialFilter.AttributeName);
            }
            if (geospatialFilter is NearGeospatialFilter nearGeospatialFilter)
            {
                GeoJsonPoint<GeoJsonCoordinates> point = nearGeospatialFilter.Point.ToGeoJsonPoint();
                
                _geospatialFilters.Add(OctoPipelineStageBuilder.GeoNear<RtEntity, GeoJsonCoordinates>(resolvedAttributeName, point, nearGeospatialFilter.MinDistance, nearGeospatialFilter.MaxDistance));
            }
        }
    }

    internal async Task<MultipleOriginResultSet<TTargetEntity>> ExecuteQuery(IOctoSession session, int? skip,
        int? take)
    {
        if (skip.HasValue && !take.HasValue)
        {
            throw OperationFailedException.PagingNeeded();
        }

        var connectFromRtIdField = "targetRtId";
        var connectToRtIdField = "originRtId";
        var connectToCkTypeIdField = "targetCkTypeId";
        var @as = "_associations";

        switch (_graphDirection)
        {
            case GraphDirections.Inbound:
                connectFromRtIdField = "originRtId";
                connectToRtIdField = "targetRtId";
                connectToCkTypeIdField = "originCkTypeId";
                break;
            case GraphDirections.Outbound:
                break;
            default:
                throw OperationFailedException.GraphDirectionUnsupported(_graphDirection);
        }


        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        var associationFilter = new FilterDefinitionBuilder<RtAssociation>().Eq(x => x.AssociationRoleId, _roleId);

        var startWith =
            (AggregateExpressionDefinition<RtEntity, BsonValue>)"$_id";

        AddPreStagesToPipelines(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        AddPostStagesToPipeline(pipelineStageDefinitions);

        var projectDefinition = (ProjectionDefinition<BsonDocument, BsonDocument>)
            new BsonDocument(@as,
                new BsonDocument("$sortArray",
                    new BsonDocument
                    {
                        {
                            "input",
                            new BsonDocument("$filter",
                                new BsonDocument
                                {
                                    { "input", "$" + @as },
                                    {
                                        "cond",
                                        new BsonDocument("$eq",
                                            new BsonArray
                                            {
                                                "$$this." + connectToCkTypeIdField,
                                                _targetCkTypeGraph.CkTypeId.SemanticVersionedFullName
                                            })
                                    }
                                })
                        },
                        {
                            "sortBy",
                            new BsonDocument("depth", 1)
                        }
                    }));

        var aggregate = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph).Aggregate(session)
            .Match(
                Builders<RtEntity>.Filter.And(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds)))
            .GraphLookup<RtAssociation, BsonValue, BsonValue, BsonValue, TTargetEntity, TTargetEntity[], BsonDocument>(
                _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                connectFromRtIdField,
                connectToRtIdField,
                startWith,
                depthField: "depth",
                @as: @as,
                options: new AggregateGraphLookupOptions<RtAssociation, TTargetEntity, BsonDocument>
                {
                    RestrictSearchWithMatch = associationFilter
                }
            )
            .Project(projectDefinition)
            .Unwind(@as, new AggregateUnwindOptions<BsonDocument> { PreserveNullAndEmptyArrays = true })
            .Lookup<BsonDocument, TTargetEntity, TTargetEntity, IEnumerable<TTargetEntity>, BsonDocument>(
                _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TTargetEntity>(_targetCkTypeGraph).GetMongoCollection(),
                @as + "." + connectFromRtIdField,
                "_id",
                pipelineStageDefinitions.Any()
                    ? PipelineDefinition<TTargetEntity, TTargetEntity>.Create(pipelineStageDefinitions)
                    : null,
                @as)
            .Unwind(@as, new AggregateUnwindOptions<BsonDocument> { PreserveNullAndEmptyArrays = true })
            .Group<BsonDocument>(new BsonDocument
            {
                { "_id", "$_id" },
                { @as, new BsonDocument("$addToSet", "$" + @as) }
            });

        var aggregate2 = aggregate.ReplaceWith(
            (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)
            "{ _id: '$_id', totalCount: {$size: '$_associations' }, 'targets': '$_associations'}");


        if (skip.HasValue)
        {
            var query =
                "{ _id: '$_id', totalCount: {$size: '$_associations' }, 'targets': {'$slice': ['$_associations', " +
                skip + "," + take + "]}}";
            aggregate2 = aggregate.ReplaceWith(
                (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)query);
        }
        else if (take.HasValue)
        {
            var query =
                "{ _id: '$_id', totalCount: {$size: '$_associations' }, 'targets': {'$slice': ['$_associations', 0," +
                take + "]}}";
            aggregate2 = aggregate.ReplaceWith(
                (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)query);
        }

        var result = await aggregate2.ToListAsync();

        foreach (var multipleResult in result)
        {
            multipleResult.Grouping = CalculateGrouping(multipleResult.Targets);
        }

        return new MultipleOriginResultSet<TTargetEntity>(result);
    }
}