using System.Collections.Concurrent;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     MongoDB CRUD operations' implementation.
/// </summary>
public class MongoRepository : IRepositoryInternal
{
    private readonly IGridFSBucket _bucket;
    private readonly ConcurrentDictionary<Type, string> _collectionNameMapping = new();

    private readonly IMongoDatabase _database;

    public MongoRepository(IMongoDatabase mongoDatabase)
    {
        _database = mongoDatabase;

        // Do not do here any commands that access the database. At initial 
        // setups the user might not have been already created.

        _bucket = new GridFSBucket(_database, new GridFSBucketOptions
        {
            WriteConcern = WriteConcern.WMajority,
            ReadPreference = ReadPreference.SecondaryPreferred
        });
    }


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

            await _database.CreateCollectionAsync(name, options);
        }
    }

    public IMongoDbDataSourceCollection<TKey, TDocument> GetCollection<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        var name = GetCollectionName(mongoDataSourceMapper, suffix);

        return new MongoDbDataSourceCollection<TKey, TDocument>(_database.GetCollection<TDocument>(name),
            mongoDataSourceMapper);
    }

    public async Task<OctoObjectId> UploadLargeBinaryAsync(IOctoSession session, string filename, string contentType, BinaryType binaryType,
        Stream stream, CancellationToken cancellationToken = default)
    {
        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { Constants.ContentType, contentType },
                { Constants.BinaryType, binaryType }
            }
        };

        return (await _bucket.UploadFromStreamAsync(filename, stream, options, cancellationToken)).ToOctoObjectId();
    }

    public async Task ReplaceLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId, string filename, string contentType,
        BinaryType binaryType, Stream stream, CancellationToken cancellationToken = default)
    {
        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { Constants.ContentType, contentType },
                { Constants.BinaryType, binaryType }
            }
        };

        await _bucket.UploadFromStreamAsync(largeBinaryId.ToObjectId(), filename, stream, options, cancellationToken);
    }

    public async Task<OctoObjectId> ReplaceLargeBinaryAsync(IOctoSession session, string filename, string contentType, BinaryType binaryType,
        Stream stream, CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Filename", filename);
        filter &= Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.BinaryType, (int)binaryType);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
        var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        if (gridFsFileInfo != null) await _bucket.DeleteAsync(gridFsFileInfo.Id, cancellationToken);
        var largeBinaryId = ObjectId.GenerateNewId();

        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { Constants.ContentType, contentType },
                { Constants.BinaryType, binaryType }
            }
        };

        await _bucket.UploadFromStreamAsync(largeBinaryId, filename, stream, options, cancellationToken);

        return largeBinaryId.ToOctoObjectId();
    }

    public async Task DeleteLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        await _bucket.DeleteAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        var gridFsDownloadStream =
            await _bucket.OpenDownloadStreamAsync(largeBinaryId.ToObjectId(), cancellationToken: cancellationToken);
        return new DownloadStreamHandler(gridFsDownloadStream);
    }

    public async Task<IBinaryInfo?> GetLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("_id", largeBinaryId);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
        var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        if (gridFsFileInfo == null) return null;
        return new BinaryInfo(gridFsFileInfo);
    }

    public async Task<IBinaryInfo?> GetLargeBinaryAsync(IOctoSession session, string fileName, BinaryType binaryType,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Filename", fileName);
        filter &= Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.BinaryType, (int)binaryType);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
        var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        if (gridFsFileInfo == null) return null;
        return new BinaryInfo(gridFsFileInfo);
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

    private async Task<bool> CollectionExistsAsync<TKey, TDocument>(
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper,
        string? suffix = null)
        where TKey : notnull
        where TDocument : class, new()
    {
        var name = GetCollectionName(mongoDataSourceMapper, suffix);

        var filter = new BsonDocument("name", name);
        //filter by collection name
        var collections = await _database.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        //check for existence
        return await collections.AnyAsync();
    }

    private bool IsVersionGreaterOrEqual(int majorVersion)
    {
        var command = new BsonDocument("buildInfo", 1);
        var result = _database.RunCommand<BsonDocument>(command);
        var version = result["version"].AsString;

        var majorVersionString = version.Split('.')[0];
        if (int.TryParse(majorVersionString, out var tmp)) return tmp >= majorVersion;

        return false;
    }
}
