using System.Collections.Concurrent;
using System.Diagnostics;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     The indexes of a collection as <c>listIndexes</c> returns them, cached per database and collection in the process
///     (AB#9138). The query path reads them to decide on an index hint (<see cref="SortIndexHint" />) without a
///     <c>listIndexes</c> per query: one per collection and process per <see cref="Ttl" />. Every index change this process
///     makes through <c>MongoDbDataSourceCollection</c> invalidates the collection at once (<see cref="Invalidate" />):
///     every entry carries the invalidation generation it was read in and is used only while that generation is still
///     current, so a list read across an invalidation is never reused, whenever it is stored. Another process sees
///     a change after at most <see cref="Ttl" />; a hint whose index no longer exists is retried without it by the caller.
///     When the indexes cannot be listed the answer is null (no hint); a failure is remembered for
///     <see cref="FailureTtl" /> only, so a transient error does not switch the hint off for the whole interval. The cache
///     holds one small entry per collection the process paged.
/// </summary>
internal static class CollectionIndexCache
{
    private static readonly
        ConcurrentDictionary<string, (IReadOnlyList<BsonDocument>? Indexes, long CheckedAt, long Version)> Cache = new();

    /// <summary>Incremented by every invalidation of a collection; an entry of an older generation is never used.</summary>
    private static readonly ConcurrentDictionary<string, long> Versions = new();

    /// <summary>How long a list of indexes is reused before it is read again.</summary>
    internal static TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a failed <c>listIndexes</c> is remembered before it is tried again.</summary>
    internal static TimeSpan FailureTtl { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The indexes of <paramref name="collection" /> (cached), or null when they could not be listed.</summary>
    internal static async Task<IReadOnlyList<BsonDocument>?> GetAsync<TDocument>(IMongoCollection<TDocument> collection,
        CancellationToken cancellationToken = default)
    {
        var key = Key(collection);
        var now = Stopwatch.GetTimestamp();
        // Read before the cache and before listIndexes: an invalidation after this point raises the generation, so an
        // entry stored below with this one is ignored by every later call (no window between check and store).
        var version = Versions.GetValueOrDefault(key);
        if (Cache.TryGetValue(key, out var cached) && cached.Version == version &&
            Stopwatch.GetElapsedTime(cached.CheckedAt, now) < (cached.Indexes == null ? FailureTtl : Ttl))
        {
            return cached.Indexes;
        }

        IReadOnlyList<BsonDocument>? indexes;
        try
        {
            using var cursor = await collection.Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
            indexes = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            indexes = null;
        }

        Cache[key] = (indexes, now, version);

        return indexes;
    }

    /// <summary>Forgets the indexes of a collection whose indexes this process just changed.</summary>
    internal static void Invalidate<TDocument>(IMongoCollection<TDocument> collection)
    {
        var key = Key(collection);
        Versions.AddOrUpdate(key, 1, (_, v) => v + 1);
        Cache.TryRemove(key, out _);
    }

    private static string Key<TDocument>(IMongoCollection<TDocument> collection) =>
        collection.CollectionNamespace.DatabaseNamespace.DatabaseName + "/" +
        collection.CollectionNamespace.CollectionName;
}
