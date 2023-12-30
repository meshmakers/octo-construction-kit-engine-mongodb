using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     MongoDB CRUD operations implementation.
/// </summary>
public class MongoRepository : IRepositoryInternal
{
    private readonly IGridFSBucket _bucket;
    private readonly Dictionary<Type, string> _collectionNameMapping = new();

    private readonly IMongoDatabase _database;

    public MongoRepository(IMongoDatabase mongoDatabase)
    {
        _database = mongoDatabase;

        _bucket = new GridFSBucket(_database, new GridFSBucketOptions
        {
            WriteConcern = WriteConcern.WMajority,
            ReadPreference = ReadPreference.SecondaryPreferred
        });
    }


    public async Task CreateCollectionIfNotExistsAsync<TCollection>(bool enableChangeStreamPreAndPostImages, string? suffix = null)
        where TCollection : class, new()
    {
        if (!await CollectionExistsAsync<TCollection>(suffix))
        {
            var name = GetCollectionName<TCollection>(suffix);
            var options = new CreateCollectionOptions
            {
                ChangeStreamPreAndPostImagesOptions = new ChangeStreamPreAndPostImagesOptions
                {
                    Enabled = enableChangeStreamPreAndPostImages
                }
            };
            await _database.CreateCollectionAsync(name, options);
        }
    }

    public IMongoDbDataSourceCollection<TKey, T> GetCollection<TKey, T>(IMongoDataSourceMapper<TKey, T> mongoDataSourceMapper,
        string? suffix = null)        where TKey : notnull

        where T : class, new()
    {
        var name = GetCollectionName<T>(suffix);

        return new MongoDbDataSourceCollection<TKey, T>(_database.GetCollection<T>(name), mongoDataSourceMapper);
    }

    public string GetCollectionName<T>(string? suffix = null) where T : class, new()
    {
        if (!_collectionNameMapping.TryGetValue(typeof(T), out var name))
        {
            name = typeof(T).GetMostInnerBaseType().Name;
            _collectionNameMapping.Add(typeof(T), name);
        }

        if (!string.IsNullOrEmpty(suffix)) return name + "_" + suffix;

        return name;
    }

    public async Task<ObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default)
    {
        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { "contentType", contentType }
            }
        };

        return await _bucket.UploadFromStreamAsync(filename, stream, options, cancellationToken);
    }

    public async Task ReplaceLargeBinaryAsync(ObjectId largeBinaryId, string filename, string contentType,
        Stream stream, CancellationToken cancellationToken = default)
    {
        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { Constants.ContentType, contentType }
            }
        };

        await _bucket.UploadFromStreamAsync(largeBinaryId, filename, stream, options, cancellationToken);
    }

    public async Task DeleteLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default)
    {
        await _bucket.DeleteAsync(largeBinaryId, cancellationToken);
    }

    public async Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        var gridFsDownloadStream =
            await _bucket.OpenDownloadStreamAsync(largeBinaryId, cancellationToken: cancellationToken);
        return new DownloadStreamHandler(gridFsDownloadStream);
    }

    public async Task<IDownloadInfo> GetLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("_id", largeBinaryId);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
        var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        return new DownloadInfo(gridFsFileInfo);
    }

    private async Task<bool> CollectionExistsAsync<T>(string? suffix = null) where T : class, new()
    {
        var name = GetCollectionName<T>(suffix);

        var filter = new BsonDocument("name", name);
        //filter by collection name
        var collections = await _database.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        //check for existence
        return await collections.AnyAsync();
    }
}