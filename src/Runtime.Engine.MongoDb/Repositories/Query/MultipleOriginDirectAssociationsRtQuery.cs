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
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class MultipleOriginDirectAssociationsRtQuery : MultipleOriginDirectAssociationsRtQuery<RtEntity>
{
    internal MultipleOriginDirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkTypeGraph targetCkTypeGraph)
        : base(ckCacheService, tenantId, mongoDbRepositoryDataSource, language, rtIds, originCkTypeGraph, roleId,
            graphDirection,
            targetCkTypeGraph)
    {
    }
}

internal class MultipleOriginDirectAssociationsRtQuery<TTargetEntity> : Query<TTargetEntity>
    where TTargetEntity : RtEntity, new()
{
    private readonly GraphDirections _graphDirection;
    private readonly ICkCacheService _ckCacheService;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly CkTypeGraph _originCkTypeGraph;
    private readonly CkId<CkAssociationRoleId> _roleId;
    private readonly IEnumerable<OctoObjectId> _rtIds;
    private readonly CkTypeGraph _targetCkTypeGraph;
    private readonly List<IPipelineStageDefinition> _geospatialFilters;

    internal MultipleOriginDirectAssociationsRtQuery(ICkCacheService ckCacheService, string tenantId,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph,
        CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkTypeGraph targetCkTypeGraph)
        : base(new RtEntityFieldFilterResolver<TTargetEntity>(ckCacheService, tenantId, targetCkTypeGraph), language)
    {
        _ckCacheService = ckCacheService;
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
            var resolvedAttributeName = _fieldFilterResolver.ResolveAttributePath(geospatialFilter.AttributeName);
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

    internal async Task<IMultipleOriginResultSet<TTargetEntity>> ExecuteQuery(IOctoSession session, int? skip,
        int? take)
    {
        if (skip.HasValue && !take.HasValue)
        {
            throw OperationFailedException.PagingNeeded();
        }

        var innerLocalFieldRtId = (FieldDefinition<RtAssociation>)"targetRtId";
        var innerLocalFieldCkId = "targetCkTypeId";
        var connectToField = (FieldDefinition<RtAssociation, string>)"originRtId";

        switch (_graphDirection)
        {
            case GraphDirections.Inbound:
                innerLocalFieldRtId = "originRtId";
                innerLocalFieldCkId = "originCkTypeId";
                connectToField = "targetRtId";
                break;
            case GraphDirections.Outbound:
                break;
            default:
                throw OperationFailedException.GraphDirectionUnsupported(_graphDirection);
        }

        var connectFromField = (FieldDefinition<RtEntity, string[]>)"_id";
        var @as = (FieldDefinition<BsonDocument, TTargetEntity[]>)"_associations";

        var targetCkIds = _targetCkTypeGraph.GetAllDerivedTypes(true);
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>([
            PipelineStageDefinitionBuilder.Match(
                Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq("associationRoleId", _roleId),
                    Builders<RtAssociation>.Filter.In(innerLocalFieldCkId, targetCkIds)
                )),
            PipelineStageDefinitionBuilder.Lookup(
                _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TTargetEntity>(_targetCkTypeGraph)
                    .GetMongoCollection(),
                innerLocalFieldRtId,
                "_id",
                (FieldDefinition<BsonDocument>)"target"),
            PipelineStageDefinitionBuilder.Unwind((FieldDefinition<BsonDocument>)"target"),
            PipelineStageDefinitionBuilder.ReplaceRoot<BsonDocument, TTargetEntity>("$target"),
        ]);


        AddPreStagesToPipelines(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        AddPostStagesToPipeline(pipelineStageDefinitions);


        var aggregate = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph)
            .Aggregate(session)
            .Match(
                Builders<RtEntity>.Filter.And(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds)))
            .Lookup(
                _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                connectFromField,
                connectToField,
                PipelineDefinition<RtAssociation, TTargetEntity>.Create(pipelineStageDefinitions),
                @as
            );

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

    protected override IEnumerable<GroupingResult>? CalculateGrouping( IEnumerable<TTargetEntity> resultList)
    {
        if (GroupBy == null)
        {
            return null;
        }

        var statisticFunctions = new RtStatisticFunctions<TTargetEntity>(_ckCacheService, _tenantId, GroupBy);
        return statisticFunctions.Calculate(resultList);
    }
}
