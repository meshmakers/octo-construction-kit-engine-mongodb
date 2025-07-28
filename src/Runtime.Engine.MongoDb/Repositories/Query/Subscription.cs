using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class Subscription<TEntity>(
    ICkCacheService ckCacheService,
    string tenantId,
    CkTypeGraph ckTypeGraph,
    IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : Engine<TEntity>(new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph))
    where TEntity : RtEntity, new()
{
    private readonly FieldFilterResolver<TEntity> _beforeFieldFilterResolver =
        new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph);

    internal void AddBeforeFieldFilterCriteria(FieldFilterCriteria? fieldFilterCriteria)
    {
        _beforeFieldFilterResolver.AddFieldFilterCriteria(fieldFilterCriteria);
    }
    
    public IUpdateStream<TEntity> WatchRtEntitiesAsync(UpdateTypes updateTypes, CancellationToken cancellationToken)
    {
        var beforeFilterDefinitions = CreateBeforeFilterDefinitions();
        var filterDefinitions = CreateFilterDefinitions();

        var rtCollection = mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph);

        return rtCollection.WatchAsync(updateTypes,
            filterDefinitions == null
                ? null
                : () => Builders<ChangeStreamDocument<TEntity>>.Filter.Inject("fullDocument", filterDefinitions),
            beforeFilterDefinitions == null
                ? null
                : () => Builders<ChangeStreamDocument<TEntity>>.Filter.Inject("fullDocumentBeforeChange", beforeFilterDefinitions),
            cancellationToken);
    }

    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);

        // Add filter for ck type and derived ones
        var ckTypeIds = ckTypeGraph.GetAllDerivedTypes(true);
        filters.Add(Builders<TEntity>.Filter.In(f => f.CkTypeId, ckTypeIds));
    }
    
    private FilterDefinition<TEntity>? CreateBeforeFilterDefinitions()
    {
        var filters = new List<FilterDefinition<TEntity>>();

        filters.AddRange(_beforeFieldFilterResolver.FilterDefinitions);

        // if filter constraints exist add them to the pipeline.
        if (filters.Any())
        {
            if (filters.Count == 1)
            {
                return filters.First();
            }

            return Builders<TEntity>.Filter.And(filters);
        }

        return null;
    }
}
