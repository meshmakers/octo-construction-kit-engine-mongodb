using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     MongoDB CRUD operations' implementation.
/// </summary>
public class MongoRepository(ILoggerFactory loggerFactory, IMongoDatabase mongoDatabase) : IRepositoryInternal
{
    private readonly ConcurrentDictionary<Type, string> _collectionNameMapping = new();
    private readonly ILogger<MongoRepository> _logger = loggerFactory.CreateLogger<MongoRepository>();

    IMongoDatabase IRepositoryInternal.Database => mongoDatabase;

    // Do not do here any commands that access the database. At initial
    // setups, the user might not have yet created.

    public async Task CreateCollectionIfNotExistsAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        var name = GetCollectionName(mongoDataSourceMapper, suffix);
        var existingInfo = await TryGetCollectionInfoAsync(name);
        if (existingInfo != null) return;

        var options = new CreateCollectionOptions();
        // changeStreamPreAndPostImages requires MongoDB 6.0+; the option is silently
        // unsupported on earlier versions, so we gate the option to skip 5.x clusters.
        if (IsVersionGreaterOrEqual(6))
            options.ChangeStreamPreAndPostImagesOptions = new ChangeStreamPreAndPostImagesOptions
            {
                Enabled = enableChangeStreamPreAndPostImages
            };

        await mongoDatabase.CreateCollectionAsync(name, options);
    }

    public async Task ReconcileChangeStreamPreAndPostImagesAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        if (!IsVersionGreaterOrEqual(6)) return;

        var name = GetCollectionName(mongoDataSourceMapper, suffix);
        var existingInfo = await TryGetCollectionInfoAsync(name);
        if (existingInfo == null) return;

        var currentEnabled = GetChangeStreamPreAndPostImagesEnabled(existingInfo);
        if (currentEnabled == enableChangeStreamPreAndPostImages) return;

        _logger.LogInformation(
            "Reconciling changeStreamPreAndPostImages on '{Collection}' from {Current} to {Desired}",
            name, currentEnabled, enableChangeStreamPreAndPostImages);
        var collMod = new BsonDocument
        {
            { "collMod", name },
            { "changeStreamPreAndPostImages", new BsonDocument("enabled", enableChangeStreamPreAndPostImages) }
        };
        await mongoDatabase.RunCommandAsync<BsonDocument>(collMod);
    }

    public IMongoDbDataSourceCollection<TKey, TDocument> GetCollection<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        var name = GetCollectionName(mongoDataSourceMapper, suffix);
        var logger = loggerFactory.CreateLogger<MongoDbDataSourceCollection<TKey, TDocument>>();

        return new MongoDbDataSourceCollection<TKey, TDocument>(logger, mongoDatabase.GetCollection<TDocument>(name),
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

        if (!string.IsNullOrEmpty(suffix))
        {
            return name + "_" + suffix;
        }

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

    public async Task<IReadOnlyList<string>> ListCollectionNamesAsync(string prefix)
    {
        var filter = new BsonDocument("name", new BsonDocument("$regex", $"^{prefix}"));
        var collections = await mongoDatabase.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = filter });
        return await collections.ToListAsync();
    }

    public async Task DropCollectionAsync(string collectionName)
    {
        await mongoDatabase.DropCollectionAsync(collectionName);
    }

    public async Task<long> GetCollectionDocumentCountAsync(string collectionName)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(collectionName);
        return await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
    }

    public async Task<bool> CollectionHasDocumentsAsync(string collectionName)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(collectionName);
        var result = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Limit(1)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .FirstOrDefaultAsync();
        return result != null;
    }

    /// <summary>
    ///     Returns the <c>listCollections</c> info document for the given collection name, or
    ///     <c>null</c> if the collection does not exist. Used by
    ///     <see cref="CreateCollectionIfNotExistsAsync{TKey,TDocument}"/> to detect presence
    ///     and by <see cref="ReconcileChangeStreamPreAndPostImagesAsync{TKey,TDocument}"/> to
    ///     read the current <c>changeStreamPreAndPostImages</c> option.
    /// </summary>
    private async Task<BsonDocument?> TryGetCollectionInfoAsync(string collectionName)
    {
        var filter = new BsonDocument("name", collectionName);
        var collections = await mongoDatabase.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        return await collections.FirstOrDefaultAsync();
    }

    /// <summary>
    ///     Reads <c>options.changeStreamPreAndPostImages.enabled</c> from a <c>listCollections</c>
    ///     info document. Returns <c>false</c> if the option is absent (the MongoDB default).
    /// </summary>
    private static bool GetChangeStreamPreAndPostImagesEnabled(BsonDocument collectionInfo)
    {
        if (!collectionInfo.TryGetValue("options", out var optionsValue) || !optionsValue.IsBsonDocument)
            return false;
        var options = optionsValue.AsBsonDocument;
        if (!options.TryGetValue("changeStreamPreAndPostImages", out var csValue) || !csValue.IsBsonDocument)
            return false;
        return csValue.AsBsonDocument.GetValue("enabled", BsonBoolean.False).ToBoolean();
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
