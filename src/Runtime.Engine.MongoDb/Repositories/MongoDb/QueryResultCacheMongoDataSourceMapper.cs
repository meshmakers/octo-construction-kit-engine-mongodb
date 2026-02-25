using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class QueryResultCacheMongoDataSourceMapper : IMongoDataSourceMapper<string, QueryResultCacheEntry>
{
    public string CollectionNamePrefix => "QueryResultCache";

    public string GetId(QueryResultCacheEntry document)
    {
        return document.Id;
    }

    public UpdateDefinition<QueryResultCacheEntry> ApplyUpdate(QueryResultCacheEntry document)
    {
        var update = Builders<QueryResultCacheEntry>.Update;
        List<UpdateDefinition<QueryResultCacheEntry>> list =
        [
            update.Set(p => p.Id, document.Id),
            update.Set(p => p.EntityIds, document.EntityIds),
            update.Set(p => p.TotalCount, document.TotalCount),
            update.Set(p => p.CreatedAt, document.CreatedAt)
        ];

        return update.Combine(list);
    }
}
