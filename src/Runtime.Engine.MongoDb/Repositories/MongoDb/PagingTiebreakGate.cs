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
///         way an index can change (upgrade, a rollback to an older engine, a CK import). The indexes come from
///         <see cref="CollectionIndexCache" />, the same per-process list the index hint of AB#9138 reads: one
///         <c>listIndexes</c> per collection and process per <see cref="CollectionIndexCache.Ttl" />, never one per query,
///         and one invalidation (with its generation) for both. An index change of this process invalidates the
///         collection at once; another process sees the change after at most <see cref="CollectionIndexCache.Ttl" />.
///         When <c>listIndexes</c> fails, no page of the collection that has a caller sort gets the tiebreak until the
///         next check (the behaviour before AB#8960); the failure never fails the query.
///     </para>
/// </summary>
internal static class PagingTiebreakGate
{
    /// <summary>
    ///     True when a page of <paramref name="collection" /> sorted by <paramref name="sortKeys" /> (empty: no caller
    ///     sort) gets the <c>_id</c> tiebreak. A legacy Ascending index leaves it out only when it provides the complete
    ///     sort (<see cref="SortIndexHint.Provides" />): otherwise the page sorts in memory anyway, and leaving out the
    ///     tiebreak would only lose the order of the ties. A plain Unique index leaves it out for its first key.
    /// </summary>
    internal static async Task<bool> IsEnabledAsync<TDocument>(IMongoCollection<TDocument> collection,
        IReadOnlyList<(string Path, bool Descending)> sortKeys, CancellationToken cancellationToken = default)
    {
        if (sortKeys.Count == 0)
        {
            return true;
        }

        var indexes = await CollectionIndexCache.GetAsync(collection, cancellationToken).ConfigureAwait(false);
        return indexes != null && !indexes.Any(index =>
            string.Equals(TiebreakIndexShape.LeadingKeyWithoutTiebreak(index), sortKeys[0].Path,
                StringComparison.Ordinal) &&
            ((index.TryGetValue("unique", out var unique) && unique.ToBoolean()) ||
             SortIndexHint.Provides(index, sortKeys)));
    }
}
