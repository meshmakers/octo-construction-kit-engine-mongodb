using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     AB#6538: index on <c>fs.files.metadata.rtEntityId</c>. The owner lookup of linked binaries
///     (<c>DeleteAllFileSystemBinariesAsync</c>, the orphan sweep) filters on this field; without the index each lookup
///     is a collection scan.
/// </summary>
internal static class GridFsOwnerIndex
{
    /// <summary>Name of the GridFS files collection (default bucket).</summary>
    public const string FilesCollectionName = "fs.files";

    /// <summary>Name of the index.</summary>
    public const string IndexName = "metadata_rtEntityId_1";

    /// <summary>
    ///     Creates the index when it does not exist. Sparse, because temporary binaries carry no owner. Creating an index
    ///     with an identical definition is a no-op in MongoDB, so the call is safe to repeat and to run concurrently.
    /// </summary>
    public static async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var collection = database.GetCollection<BsonDocument>(FilesCollectionName);
        var model = new CreateIndexModel<BsonDocument>(
            new BsonDocument("metadata." + Constants.RtEntityId, 1),
            new CreateIndexOptions { Name = IndexName, Sparse = true });
        await collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
