using System.Collections.Concurrent;
using System.Diagnostics;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Decides per collection and sort key whether a page sorts with the <c>_id</c> tiebreak (AB#8960). A page sorted by
///     a key whose order an index provides that the tiebreak would break - a CK Ascending index in the legacy shape, or a
///     plain Unique index on that key (<see cref="TiebreakIndexShape.LeadingKeyWithoutTiebreak" />) - pages without it:
///     there the tiebreak would turn an index-backed page into a sort over every matching document (measured 2 ms to
///     113 ms for the first page over 100,000 entities). Every other page gets the tiebreak at once: an unsorted page
///     (sorted by <c>_id</c>, which the System Entity index <c>{ckTypeId, _id, rtState}</c> provides on every collection)
///     and a page sorted by an attribute no such index leads with (a blocking sort before as well). Once the legacy
///     indexes of a collection are rebuilt in the tiebreak shape - <c>ITenantContext.UpgradeTiebreakIndexesAsync</c>, or
///     created in that shape right away for new tenants and new indexes - those pages get the tiebreak too.
///     <para>
///         The answer comes from the indexes themselves (<c>listIndexes</c>), so it needs no marker and follows every
///         way an index can change (upgrade, a rollback to an older engine, a CK import). It is cached per database and
///         collection in the process for <see cref="Ttl" />: one <c>listIndexes</c> per collection and process per
///         interval, never one per query. An index update of this process invalidates the collections it touched at
///         once; another process sees the change after at most <see cref="Ttl" />. When <c>listIndexes</c> fails, no page
///         of the collection that has a caller sort gets the tiebreak until the next check (the behaviour before
///         AB#8960); the failure never fails the query. The cache holds one small entry per collection the process
///         paged.
///     </para>
/// </summary>
internal static class PagingTiebreakGate
{
    private static readonly ConcurrentDictionary<string, (IReadOnlySet<string>? LegacyKeys, long CheckedAt)> Cache =
        new();

    /// <summary>How long a decision is reused before the indexes are listed again.</summary>
    internal static TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     True when a page of <paramref name="collection" /> sorted first by <paramref name="firstSortKeyPath" /> (null:
    ///     no caller sort) gets the <c>_id</c> tiebreak.
    /// </summary>
    internal static async Task<bool> IsEnabledAsync<TDocument>(IMongoCollection<TDocument> collection,
        string? firstSortKeyPath, CancellationToken cancellationToken = default)
    {
        if (firstSortKeyPath == null)
        {
            return true;
        }

        var legacyKeys = await LegacyLeadingKeysAsync(collection, cancellationToken).ConfigureAwait(false);
        return legacyKeys != null && !legacyKeys.Contains(firstSortKeyPath);
    }

    /// <summary>
    ///     The keys the legacy indexes of a collection provide a page order for (cached), or null when the indexes
    ///     could not be listed.
    /// </summary>
    private static async Task<IReadOnlySet<string>?> LegacyLeadingKeysAsync<TDocument>(
        IMongoCollection<TDocument> collection, CancellationToken cancellationToken)
    {
        var key = Key(collection.CollectionNamespace.DatabaseNamespace.DatabaseName,
            collection.CollectionNamespace.CollectionName);
        var now = Stopwatch.GetTimestamp();
        if (Cache.TryGetValue(key, out var cached) && Stopwatch.GetElapsedTime(cached.CheckedAt, now) < Ttl)
        {
            return cached.LegacyKeys;
        }

        IReadOnlySet<string>? legacyKeys;
        try
        {
            using var cursor = await collection.Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
            var indexes = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
            legacyKeys = indexes
                .Select(TiebreakIndexShape.LeadingKeyWithoutTiebreak)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            legacyKeys = null;
        }

        Cache[key] = (legacyKeys, now);
        return legacyKeys;
    }

    /// <summary>Forgets the decision for a collection whose indexes this process just changed.</summary>
    internal static void Invalidate(string databaseName, string collectionName) =>
        Cache.TryRemove(Key(databaseName, collectionName), out _);

    private static string Key(string databaseName, string collectionName) => databaseName + "/" + collectionName;
}
