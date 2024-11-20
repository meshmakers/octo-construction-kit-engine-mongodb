using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public interface IMongoDbDataSourceCollection<TKey, TDocument> : IDataSourceCollection<TKey, TDocument>
    where TDocument : class, new()
    where TKey : notnull
{
    string CollectionName { get; }
    
    IMongoDataSourceMapper<TKey, TDocument> MongoDataSourceMapper { get; }

    Task CreateAscendingIndexAsync(string name, IEnumerable<string> fields);
    Task CreateTextIndexAsync(string name, string language, IEnumerable<CkIndexFields> fields);
    Task DropIndexAsync(string name);

    Task<ICollection<TDocument>> FindManyAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition,
        SortDefinition<TDocument>? sort = null, int? skip = null, int? take = null);

    Task UpdateOneAsync(IOctoSession session, TKey id, UpdateDefinition<TDocument> updateDefinition);

    Task DeleteOneAsync(IOctoSession session, FilterDefinition<TDocument> filter);

    Task DeleteManyAsync(IOctoSession session, FilterDefinition<TDocument> filter);

    Task ReplaceOneAsync(IOctoSession session, FilterDefinition<TDocument> filter, TDocument entity);

    IUpdateStream<TDocument> WatchAsync(UpdateTypes updateTypes,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentFilterFunc = null,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentBeforeFilterFunc = null,
        CancellationToken cancellationToken = default);

    IAggregateFluent<TDocument> Aggregate(IOctoSession session);

    IAsyncCursor<TOutput> Aggregate<TOutput>(IOctoSession session,
        PipelineDefinition<TDocument, TOutput> pipelineDefinition);

    Task<long> GetTotalCountAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition);

    IMongoCollection<TDocument> GetMongoCollection();
}