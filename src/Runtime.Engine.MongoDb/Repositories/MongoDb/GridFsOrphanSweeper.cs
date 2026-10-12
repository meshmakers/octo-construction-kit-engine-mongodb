using System.Diagnostics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Options of <see cref="GridFsOrphanSweeper" />.
/// </summary>
public sealed class GridFsOrphanSweepOptions
{
    /// <summary>
    ///     Shortest allowed grace period: ten times the MongoDB transaction lifetime (60 s), so that bytes staged by an
    ///     in-flight transaction are never touched.
    /// </summary>
    public static readonly TimeSpan MinimumGracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>Default grace period.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromHours(24);

    /// <summary>
    ///     When <c>false</c> (default) the sweep only reports. When <c>true</c> orphaned files and their chunks are
    ///     deleted.
    /// </summary>
    public bool Remove { get; init; }

    /// <summary>
    ///     Only files older than this are considered. Defaults to 24 h; values below <see cref="MinimumGracePeriod" />
    ///     are refused.
    /// </summary>
    public TimeSpan GracePeriod { get; init; } = DefaultGracePeriod;
}

/// <summary>
///     Result of a <see cref="GridFsOrphanSweeper" /> run. Contains counts and sizes only.
/// </summary>
public sealed class GridFsOrphanSweepResult
{
    /// <summary><c>true</c> when files were removed, <c>false</c> for a dry run.</summary>
    public bool Removed { get; init; }

    /// <summary>Grace period that was applied.</summary>
    public TimeSpan GracePeriod { get; init; }

    /// <summary>FileSystem files older than the grace period that were examined.</summary>
    public long ExaminedFiles { get; init; }

    /// <summary>Examined files that are referenced by an entity.</summary>
    public long ReferencedFiles { get; init; }

    /// <summary>FileSystem files younger than the grace period; not examined.</summary>
    public long YoungFiles { get; init; }

    /// <summary>Orphaned files (found; removed when <see cref="Removed" />).</summary>
    public long OrphanFiles { get; init; }

    /// <summary>Chunks belonging to the orphaned files.</summary>
    public long OrphanChunks { get; init; }

    /// <summary>Orphaned files that were deleted by this run (0 for a dry run).</summary>
    public long RemovedFiles { get; init; }

    /// <summary>Total size in bytes of the orphaned files.</summary>
    public long OrphanBytes { get; init; }

    /// <summary>Duration of the run.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
///     AB#6541: finds and removes orphaned GridFS bytes of linked binaries. After AB#6248 the entity references the
///     stored <c>BinaryId</c>; a FileSystem file that no entity references is an orphan, whatever its
///     <c>metadata.rtEntityId</c> says (old bytes after a crash between commit and delete, staged bytes after a crash
///     before commit, bytes without owner metadata, bytes of deleted entities). Temporary binaries are never touched.
/// </summary>
/// <remarks>
///     Dry run is the default. Only files older than the grace period (default 24 h, minimum 10 min) are considered;
///     the candidate list is taken before the references are read, so bytes that an in-flight transaction stages (and
///     that are younger than the grace period anyway) are never removed. The run is idempotent and safe to repeat.
///     References are read server side as the ObjectIds found in <c>attributes.*.binaryId</c> of every
///     <c>RtEntity*</c> collection (one pass, only ids are returned). Logs ids and counts only.
/// </remarks>
public class GridFsOrphanSweeper
{
    private const int BatchSize = 500;

    private readonly IMongoDatabase _database;
    private readonly IGridFSBucket _bucket;
    private readonly ILogger _logger;

    /// <summary>Creates a sweeper for the database of one tenant.</summary>
    public GridFsOrphanSweeper(IRepositoryClient repositoryClient, string databaseName, ILogger<GridFsOrphanSweeper> logger)
        : this(GetRepository(repositoryClient, databaseName), logger)
    {
    }

    private GridFsOrphanSweeper(MongoRepository repository, ILogger logger)
    {
        _database = repository.Database;
        _bucket = repository.GetGridFsBucket();
        _logger = logger;
    }

    private static MongoRepository GetRepository(IRepositoryClient repositoryClient, string databaseName)
    {
        return (MongoRepository)repositoryClient.GetRepository(databaseName);
    }

    /// <summary>Runs the sweep.</summary>
    public async Task<GridFsOrphanSweepResult> SweepAsync(GridFsOrphanSweepOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GridFsOrphanSweepOptions();
        if (options.GracePeriod < GridFsOrphanSweepOptions.MinimumGracePeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"The grace period must be at least {GridFsOrphanSweepOptions.MinimumGracePeriod} (10x the transaction lifetime).");
        }

        var stopwatch = Stopwatch.StartNew();
        var cutoff = DateTime.UtcNow - options.GracePeriod;
        var files = _database.GetCollection<BsonDocument>("fs.files");
        var chunks = _database.GetCollection<BsonDocument>("fs.chunks");

        // 1. Candidates first (FileSystem files older than the grace period), then the references. A file that is
        // older than the grace period cannot become referenced later: references are written in the same
        // transaction that uploaded the bytes, which is far shorter than the grace period.
        var fileSystemFilter = Builders<BsonDocument>.Filter.Eq("metadata." + Constants.BinaryType, (int)BinaryType.FileSystem);
        var candidates = new Dictionary<ObjectId, long>();
        long young = 0;
        var cursor = await files.Find(fileSystemFilter)
            .Project(Builders<BsonDocument>.Projection.Include("length").Include("uploadDate"))
            .ToCursorAsync(cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var doc in cursor.Current)
            {
                if (doc["uploadDate"].ToUniversalTime() >= cutoff)
                {
                    young++;
                    continue;
                }

                candidates[doc["_id"].AsObjectId] = doc.GetValue("length", 0).ToInt64();
            }
        }

        // 2. References.
        var referenced = await ReadReferencedIdsAsync(cancellationToken).ConfigureAwait(false);

        // 3. Orphans.
        var orphans = candidates.Where(x => !referenced.Contains(x.Key)).ToList();
        long orphanChunks = 0;
        long removedFiles = 0;
        foreach (var batch in orphans.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = batch.Select(x => x.Key).ToList();
            orphanChunks += await chunks.CountDocumentsAsync(Builders<BsonDocument>.Filter.In("files_id", ids),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (options.Remove)
            {
                foreach (var id in ids)
                {
                    try
                    {
                        await _bucket.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                        removedFiles++;
                    }
                    catch (GridFSFileNotFoundException)
                    {
                        // Already gone (concurrent run); the sweep is idempotent.
                    }
                }

                // A crash between the files and the chunks delete would leave chunks without file.
                await chunks.DeleteManyAsync(Builders<BsonDocument>.Filter.In("files_id", ids), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        stopwatch.Stop();
        var result = new GridFsOrphanSweepResult
        {
            Removed = options.Remove,
            GracePeriod = options.GracePeriod,
            ExaminedFiles = candidates.Count,
            ReferencedFiles = candidates.Count - orphans.Count,
            YoungFiles = young,
            OrphanFiles = orphans.Count,
            OrphanChunks = orphanChunks,
            RemovedFiles = removedFiles,
            OrphanBytes = orphans.Sum(x => x.Value),
            Duration = stopwatch.Elapsed
        };

        _logger.LogInformation(
            "GridFS orphan sweep of '{Database}' ({Mode}): examined {Examined}, referenced {Referenced}, young {Young}, orphans {Orphans} ({Chunks} chunks, {Bytes} bytes), removed {Removed}, duration {Duration}",
            _database.DatabaseNamespace.DatabaseName, options.Remove ? "remove" : "dry-run", result.ExaminedFiles,
            result.ReferencedFiles, result.YoungFiles, result.OrphanFiles, result.OrphanChunks, result.OrphanBytes,
            removedFiles, result.Duration);

        return result;
    }

    private async Task<HashSet<ObjectId>> ReadReferencedIdsAsync(CancellationToken cancellationToken)
    {
        var referenced = new HashSet<ObjectId>();
        var namesCursor = await _database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity"))
            }, cancellationToken).ConfigureAwait(false);
        var names = await namesCursor.ToListAsync(cancellationToken).ConfigureAwait(false);

        // Server side: per entity only the ObjectIds in attributes.<any>.binaryId are returned, never whole documents.
        var pipeline = new[]
        {
            new BsonDocument("$project", new BsonDocument("ids", new BsonDocument("$map", new BsonDocument
            {
                { "input", new BsonDocument("$filter", new BsonDocument
                    {
                        { "input", new BsonDocument("$objectToArray", new BsonDocument("$ifNull", new BsonArray { "$attributes", new BsonDocument() })) },
                        { "cond", new BsonDocument("$eq", new BsonArray
                            {
                                new BsonDocument("$type", "$$this.v.binaryId"),
                                "objectId"
                            }) }
                    }) },
                { "in", "$$this.v.binaryId" }
            }))),
            new BsonDocument("$match", new BsonDocument("ids.0", new BsonDocument("$exists", true))),
            new BsonDocument("$unwind", "$ids"),
            new BsonDocument("$group", new BsonDocument("_id", "$ids"))
        };

        foreach (var name in names)
        {
            var collection = _database.GetCollection<BsonDocument>(name);
            using var cursor = await collection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var doc in cursor.Current)
                {
                    referenced.Add(doc["_id"].AsObjectId);
                }
            }
        }

        return referenced;
    }
}
