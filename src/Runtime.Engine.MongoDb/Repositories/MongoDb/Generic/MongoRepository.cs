using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     MongoDB CRUD operations' implementation.
/// </summary>
public class MongoRepository(IMongoDatabase mongoDatabase) : IRepositoryInternal
{
    private readonly ConcurrentDictionary<Type, string> _collectionNameMapping = new();

    // Do not do here any commands that access the database. At initial
    // setups, the user might not have yet created.

    public async Task CreateCollectionIfNotExistsAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        if (!await CollectionExistsAsync(mongoDataSourceMapper, suffix))
        {
            var name = GetCollectionName(mongoDataSourceMapper, suffix);
            var options = new CreateCollectionOptions();
            if (IsVersionGreaterOrEqual(5))
                options.ChangeStreamPreAndPostImagesOptions = new ChangeStreamPreAndPostImagesOptions
                {
                    Enabled = enableChangeStreamPreAndPostImages
                };

            await mongoDatabase.CreateCollectionAsync(name, options);
        }
    }

    public IMongoDbDataSourceCollection<TKey, TDocument> GetCollection<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        var name = GetCollectionName(mongoDataSourceMapper, suffix);

        return new MongoDbDataSourceCollection<TKey, TDocument>(mongoDatabase.GetCollection<TDocument>(name),
            mongoDataSourceMapper);
    }

    public string GetCollectionName<TKey, TDocument>(IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        if (!_collectionNameMapping.TryGetValue(typeof(TDocument), out var name))
        {
            name = mongoDataSourceMapper.CollectionNamePrefix;
            _collectionNameMapping.TryAdd(typeof(TDocument), name);
        }

        if (!string.IsNullOrEmpty(suffix)) return name + "_" + suffix;

        return name;
    }

    public IGridFSBucket GetGridFsBucket()
    {
        return new GridFSBucket(mongoDatabase, new GridFSBucketOptions
        {
            WriteConcern = WriteConcern.WMajority,
            ReadPreference = ReadPreference.SecondaryPreferred
        });
    }

    private async Task<bool> CollectionExistsAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        var name = GetCollectionName(mongoDataSourceMapper, suffix);

        var filter = new BsonDocument("name", name);
        //filter by collection name
        var collections = await mongoDatabase.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        //check for existence
        return await collections.AnyAsync();
    }

    private bool IsVersionGreaterOrEqual(int majorVersion)
    {
        var command = new BsonDocument("buildInfo", 1);
        var result = mongoDatabase.RunCommand<BsonDocument>(command);
        var version = result["version"].AsString;

        var majorVersionString = version.Split('.')[0];
        if (int.TryParse(majorVersionString, out var tmp)) return tmp >= majorVersion;

        return false;
    }
}
