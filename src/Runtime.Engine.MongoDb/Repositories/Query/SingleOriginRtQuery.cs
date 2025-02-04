using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class SingleOriginRtQuery<TEntity> : SingleOriginQuery<OctoObjectId, TEntity> where TEntity : RtEntity, new()
{
    private readonly CkTypeGraph _ckTypeGraph;
    private readonly List<IPipelineStageDefinition> _geospatialFilters;

    internal SingleOriginRtQuery(IMetricsContext metricsContext, ICkCacheService ckCacheService, string tenantId,
        CkTypeGraph ckTypeGraph,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, string language)
        : base(metricsContext, mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph),
            new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph), language)
    {
        _ckTypeGraph = ckTypeGraph;
        _geospatialFilters = new List<IPipelineStageDefinition>();
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
                
                _geospatialFilters.Add(OctoPipelineStageBuilder.GeoNear<TEntity, GeoJsonCoordinates>(resolvedAttributeName, point, nearGeospatialFilter.MinDistance, nearGeospatialFilter.MaxDistance));
            }
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