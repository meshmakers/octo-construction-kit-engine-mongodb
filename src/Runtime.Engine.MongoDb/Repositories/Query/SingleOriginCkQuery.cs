using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class SingleOriginCkQuery<TKey, TEntity>(
    IMetricsContext metricsContext,
    IMongoDbDataSourceCollection<TKey, TEntity> mongoDbDataSourceCollection)
    : SingleOriginQuery<TKey, TEntity>(metricsContext, mongoDbDataSourceCollection,
        new FieldFilterResolver<TEntity>())
    where TEntity : class, new()
    where TKey : notnull
{
    private readonly List<FilterDefinition<TEntity>> _modelIdFilters = new();
    

    internal void AddModelIdFilter(IReadOnlyList<CkModelId>? modelIds)
    {
        if (modelIds == null || !modelIds.Any())
        {
            return;
        }

        _modelIdFilters.Add(Builders<TEntity>.Filter.In(Constants.ModelIdField, modelIds));
    }
    
    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);

        filters.AddRange(_modelIdFilters);
    }
}

