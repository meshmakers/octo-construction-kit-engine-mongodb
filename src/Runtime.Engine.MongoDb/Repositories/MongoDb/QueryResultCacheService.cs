using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class QueryResultCacheService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static bool _ttlIndexCreated;
    private static readonly Lock TtlIndexLock = new();

    private readonly IMongoCollection<QueryResultCacheEntry> _collection;

    internal QueryResultCacheService(IRepository repository)
    {
        var mapper = new QueryResultCacheMongoDataSourceMapper();
        var dataSourceCollection = repository.GetCollection(mapper);
        _collection = dataSourceCollection.GetMongoCollection();
        EnsureTtlIndex();
    }

    internal async Task<(List<OctoObjectId> EntityIds, long TotalCount)?> TryGetAsync(string cacheKey)
    {
        var filter = Builders<QueryResultCacheEntry>.Filter.Eq(e => e.Id, cacheKey);
        var entry = await _collection.Find(filter).FirstOrDefaultAsync();
        if (entry == null)
        {
            return null;
        }

        return (entry.EntityIds, entry.TotalCount);
    }

    internal async Task StoreAsync(string cacheKey, List<OctoObjectId> entityIds)
    {
        var entry = new QueryResultCacheEntry
        {
            Id = cacheKey,
            EntityIds = entityIds,
            TotalCount = entityIds.Count,
            CreatedAt = DateTime.UtcNow
        };

        var filter = Builders<QueryResultCacheEntry>.Filter.Eq(e => e.Id, cacheKey);
        await _collection.ReplaceOneAsync(filter, entry, new ReplaceOptions { IsUpsert = true });
    }

    internal static string ComputeCacheKey(
        RtCkId<CkTypeId> ckTypeId,
        RtEntityQueryOptions queryOptions,
        ICollection<NavigationPair> navigationPairs)
    {
        var sb = new StringBuilder();
        sb.Append("ckType:").Append(ckTypeId);
        sb.Append("|navMode:").Append(queryOptions.NavigationFilterMode);
        sb.Append("|includeArchived:").Append(queryOptions.GlobalFilter?.IncludeArchived ?? false);

        if (queryOptions.FieldFilters != null)
        {
            sb.Append("|ff:");
            sb.Append(JsonSerializer.Serialize(queryOptions.FieldFilters));
        }

        if (queryOptions.TextSearchFilter != null)
        {
            sb.Append("|ts:").Append(queryOptions.TextSearchFilter.SearchTerm);
        }

        if (queryOptions.AttributeSearchFilter != null)
        {
            sb.Append("|as:");
            sb.Append(JsonSerializer.Serialize(queryOptions.AttributeSearchFilter));
        }

        if (queryOptions.SortOrders != null)
        {
            sb.Append("|so:");
            sb.Append(JsonSerializer.Serialize(queryOptions.SortOrders));
        }

        if (queryOptions.GeospatialFilters != null)
        {
            sb.Append("|geo:");
            sb.Append(JsonSerializer.Serialize(queryOptions.GeospatialFilters));
        }

        if (navigationPairs.Count > 0)
        {
            sb.Append("|nav:");
            AppendNavigationPairs(sb, navigationPairs);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void AppendNavigationPairs(StringBuilder sb, IEnumerable<NavigationPair> pairs)
    {
        foreach (var pair in pairs)
        {
            sb.Append(pair.CkRoleId).Append(',');
            sb.Append(pair.Direction).Append(',');
            sb.Append(pair.TargetCkTypeId);

            if (pair.AssociationCountFilter != null)
            {
                sb.Append(",acf:");
                sb.Append(pair.AssociationCountFilter.Operator).Append(':');
                sb.Append(pair.AssociationCountFilter.ComparisonValue);
            }

            if (pair.FieldFilters != null)
            {
                sb.Append(",ff:");
                sb.Append(JsonSerializer.Serialize(pair.FieldFilters));
            }

            if (pair.InnerNavigationPairs.Count > 0)
            {
                sb.Append(",[");
                AppendNavigationPairs(sb, pair.InnerNavigationPairs);
                sb.Append(']');
            }

            sb.Append(';');
        }
    }

    private void EnsureTtlIndex()
    {
        if (_ttlIndexCreated)
        {
            return;
        }

        lock (TtlIndexLock)
        {
            if (_ttlIndexCreated)
            {
                return;
            }

            var indexKeys = Builders<QueryResultCacheEntry>.IndexKeys.Ascending(e => e.CreatedAt);
            var indexOptions = new CreateIndexOptions
            {
                ExpireAfter = CacheTtl,
                Name = "ttl_createdAt"
            };

            _collection.Indexes.CreateOne(new CreateIndexModel<QueryResultCacheEntry>(indexKeys, indexOptions));
            _ttlIndexCreated = true;
        }
    }
}
