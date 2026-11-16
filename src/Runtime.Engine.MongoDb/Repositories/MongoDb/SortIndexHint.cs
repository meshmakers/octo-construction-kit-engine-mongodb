using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Chooses the index a sorted page of a runtime-entity type query is hinted to (AB#9138). A deep sorted page
///     (<c>$match</c>, <c>$sort</c>, <c>$skip</c>, <c>$limit</c>) is pushed into the find layer as a whole; MongoDB's
///     multi-planner then runs every candidate plan until one has produced the skipped documents - the candidates that
///     sort in memory read tens of thousands of documents each, and the plan cache never keeps the winner for deep pages.
///     On 909,000 entities that trial took 56-67 s for skip 50,000, the execution itself ~90 ms; with a hint the page
///     took 52 ms. The hint names an index that provides the requested order on its own, so the plan is the one the
///     planner would win with, minus the trial.
///     <para>
///         An index qualifies when its keys are <c>ckTypeId</c> (every type query filters it by equality or
///         <c>$in</c>) followed by exactly the sort keys - more keys may follow - with every direction equal to the
///         sort's or every direction reversed (a backward scan). Indexes that could return fewer documents than the
///         query or that the query cannot use are never chosen: partial (a filter the query may not imply), sparse,
///         hidden, with a collation, and text, 2dsphere or hashed keys. Without a qualifying index there is no hint and
///         the planner decides as before. Both CK Ascending index shapes qualify: the legacy
///         <c>{ckTypeId, attr…, rtState}</c> for a sort by the attributes and the tiebreak shape
///         <c>{ckTypeId, attr…, _id, rtState}</c> for a sort by the attributes followed by <c>_id</c> (AB#8960); the
///         System Entity index <c>{ckTypeId, _id, rtState}</c> serves a sort by rtId.
///     </para>
/// </summary>
internal static class SortIndexHint
{
    private const string CkTypeIdKey = "ckTypeId";

    /// <summary>
    ///     The skip from which a sorted page is hinted (<c>OctoSystemConfiguration.PagingIndexHintMinSkip</c>, set by
    ///     the tenant context; default 1000). Shallow pages keep the planner's choice: there the trial ends early, and a
    ///     selective filter may be answered faster by its own index than by a walk along the sort index. Negative: never.
    /// </summary>
    internal static int MinSkip { get; set; } = 1000;

    /// <summary>True when a page with this skip is deep enough to be hinted.</summary>
    internal static bool IsDeep(int? skip) => MinSkip >= 0 && (skip ?? 0) >= MinSkip;

    /// <summary>
    ///     The number of matching entities from which a page whose filter has an index of its own
    ///     (<see cref="HasFilterIndex" />) is still hinted (<c>OctoSystemConfiguration.PagingIndexHintMinMatches</c>,
    ///     default 50,000). Below it the planner reads the few matches through the filter's index and sorts them, which
    ///     is faster than a walk along the sort index; above it the planner's trial costs more than the walk. 0 or less:
    ///     such a page is hinted without counting.
    /// </summary>
    internal static long MinMatches { get; set; } = 50_000;

    /// <summary>
    ///     The field paths a <c>$match</c> filter constrains, through <c>$and</c>, <c>$or</c> and <c>$nor</c>
    ///     (operator documents below a field are part of that field; <c>$expr</c> and other operators name no field).
    /// </summary>
    internal static IReadOnlySet<string> FilterFields(BsonDocument filter)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        Collect(filter, fields);
        return fields;

        static void Collect(BsonDocument document, HashSet<string> fields)
        {
            foreach (var element in document)
            {
                if (element.Name is "$and" or "$or" or "$nor" && element.Value is BsonArray branches)
                {
                    foreach (var branch in branches.OfType<BsonDocument>())
                    {
                        Collect(branch, fields);
                    }
                }
                else if (!element.Name.StartsWith('$'))
                {
                    fields.Add(element.Name);
                }
            }
        }
    }

    /// <summary>
    ///     True when an index other than the sort index can serve the filter: an index (any kind, partial ones too)
    ///     whose leading key - the first after <c>ckTypeId</c> - is a filtered field other than <c>ckTypeId</c>,
    ///     <c>rtState</c> (a <c>$ne</c> on every query) and the first sort key (an index on it serves filter and sort
    ///     together). Then the planner has a real alternative to the sort index, and the hint is only safe for many
    ///     matches (<see cref="MinMatches" />).
    /// </summary>
    internal static bool HasFilterIndex(IReadOnlyList<BsonDocument> indexes, IReadOnlySet<string> filterFields,
        string? firstSortKey)
    {
        foreach (var index in indexes)
        {
            if (!index.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument key || key.ElementCount == 0)
            {
                continue;
            }

            var leading = key.Names.FirstOrDefault(n => n != CkTypeIdKey);
            if (leading != null && leading != "rtState" && leading != firstSortKey && filterFields.Contains(leading))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The first index in <paramref name="indexes" /> (<c>listIndexes</c> documents) that provides the order of
    ///     <paramref name="sortKeys" /> after the <c>ckTypeId</c> equality - its name and its key pattern - or null. The
    ///     page is hinted by the key pattern: an index rebuilt under the same name with other keys then fails the hint
    ///     (and the read is retried without it) instead of being forced on a sort it does not provide.
    /// </summary>
    internal static (string Name, BsonDocument Key)? Choose(IReadOnlyList<BsonDocument>? indexes,
        IReadOnlyList<(string Path, bool Descending)> sortKeys)
    {
        if (indexes == null || sortKeys.Count == 0)
        {
            return null;
        }

        foreach (var index in indexes)
        {
            if (Provides(index, sortKeys) && index.TryGetValue("name", out var name) && name.IsString)
            {
                return (name.AsString, index["key"].AsBsonDocument);
            }
        }

        return null;
    }

    /// <summary>True when the index is usable for every query of the type and provides the sort after <c>ckTypeId</c>.</summary>
    internal static bool Provides(BsonDocument index, IReadOnlyList<(string Path, bool Descending)> sortKeys)
    {
        if (index.Contains("partialFilterExpression") || index.Contains("collation") ||
            IsTrue(index, "sparse") || IsTrue(index, "hidden"))
        {
            return false;
        }

        if (!index.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument key ||
            key.ElementCount < sortKeys.Count + 1)
        {
            return false;
        }

        var elements = key.Elements.ToList();
        // Every key a plain direction - also those after the sort keys: a text, 2dsphere, hashed or wildcard key leaves
        // documents out of the index or cannot serve the query.
        if (elements[0].Name != CkTypeIdKey || elements.Any(e => !TryDirection(e.Value, out _) || e.Name.Contains("$**")))
        {
            return false;
        }

        bool? reversed = null;
        for (var i = 0; i < sortKeys.Count; i++)
        {
            var element = elements[i + 1];
            if (element.Name != sortKeys[i].Path || !TryDirection(element.Value, out var indexDescending))
            {
                return false;
            }

            var differs = indexDescending != sortKeys[i].Descending;
            reversed ??= differs;
            if (reversed != differs)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An ascending (1) or descending (-1) key; false for text, 2dsphere, hashed and other special keys.</summary>
    private static bool TryDirection(BsonValue value, out bool descending)
    {
        descending = false;
        if (!value.IsNumeric)
        {
            return false;
        }

        var number = value.ToDouble();
        descending = number < 0;
        return number != 0;
    }

    private static bool IsTrue(BsonDocument index, string name) =>
        index.TryGetValue(name, out var value) && value.ToBoolean();
}
