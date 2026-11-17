using System.Text.RegularExpressions;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     The shape of the CK-defined Ascending indexes on runtime-entity collections (AB#8960). A page sorts by the
///     caller's keys and then by <c>_id</c> (<see cref="Query.Query{TEntity}.CreatePagingSortDefinition" />); an index
///     can only provide that order when it holds <c>_id</c> right after the attribute fields:
///     <c>{ckTypeId, attr…, _id, rtState}</c>. <c>_id</c> goes before <c>rtState</c> - it is filtered with <c>$ne</c>, a
///     range, and a key after a range cannot provide a sort. Such an index is named <c>&lt;Type&gt;_&lt;n&gt;</c> +
///     <see cref="NameSuffix" />, so that it can be built next to the legacy index <c>&lt;Type&gt;_&lt;n&gt;</c>
///     (<c>{ckTypeId, attr…, rtState}</c>) before that one is dropped: the collection never lacks the index.
///     Unique, UniqueNotDeleted and text indexes keep their shape and name.
/// </summary>
internal static partial class TiebreakIndexShape
{
    /// <summary>The name suffix of a CK Ascending index in the tiebreak shape.</summary>
    internal const string NameSuffix = "_id";

    /// <summary>
    ///     The number of hex digits of the definition hash an index name gets when an index with another definition
    ///     holds its expected name (<c>MongoDbRepositoryDataSource.AlternateIndexName</c>).
    /// </summary>
    internal const int AlternateNameHashLength = 6;

    /// <summary>
    ///     The alternate name of <paramref name="indexName" />: the name, an underscore and the first
    ///     <see cref="AlternateNameHashLength" /> hex digits of <paramref name="hexHash" />.
    /// </summary>
    internal static string AlternateName(string indexName, string hexHash) =>
        indexName + "_" + hexHash[..AlternateNameHashLength];

    /// <summary>True when <paramref name="name" /> is an <see cref="AlternateName" /> of <paramref name="indexName" />.</summary>
    internal static bool IsAlternateNameOf(string name, string indexName) =>
        name.StartsWith(indexName + "_", StringComparison.Ordinal) &&
        AlternateSuffix().IsMatch(name[indexName.Length..]);

    private const string CkTypeIdKey = "ckTypeId";
    private const string RtStateKey = "rtState";

    /// <summary>
    ///     The numbers of the system-level indexes <c>MongoDbRepositoryDataSource</c> creates on every runtime-entity
    ///     collection outside the CK definition (<c>rtState</c>: 9000, <c>ckTypeId + rtCreatedBy</c>: 9001). They keep
    ///     their shape: no page sorts by those fields.
    /// </summary>
    private static readonly HashSet<string> SystemIndexNumbers = ["9000", "9001"];

    /// <summary>
    ///     Inserts <c>_id</c> into the keys of a CK Ascending index: right before the first <c>rtState</c> key, at the
    ///     end when there is none. Returns null when the keys already hold <c>_id</c> (e.g. the System Entity index
    ///     <c>{CkTypeId, _id}</c>): that index already is in the tiebreak shape.
    /// </summary>
    internal static List<string>? WithIdTiebreak(IEnumerable<string> keys)
    {
        var result = keys.ToList();
        if (result.Contains(Constants.IdField))
        {
            return null;
        }

        var rtState = result.FindIndex(k => string.Equals(k, RtStateKey, StringComparison.OrdinalIgnoreCase));
        result.Insert(rtState < 0 ? result.Count : rtState, Constants.IdField);
        return result;
    }

    /// <summary>
    ///     For an index document of <c>listIndexes</c>, the sort key whose page order the index provides and the
    ///     <c>_id</c> tiebreak would break - a page sorted first by that key keeps the order without the tiebreak
    ///     (<see cref="PagingTiebreakGate" />). Null for every other index. Two kinds of CK indexes (named
    ///     <c>&lt;Type&gt;_&lt;n&gt;</c>, not the system-level ones) qualify, both without <c>_id</c> among their keys:
    ///     <list type="bullet">
    ///         <item>an Ascending index in the legacy shape: its first key after <c>ckTypeId</c> (every query filters
    ///         <c>ckTypeId</c> by equality) - until the upgrade rebuilds it in the tiebreak shape;</item>
    ///         <item>a single-key Unique index (the engine's partial filter on <c>ckTypeId</c> only,
    ///         <see cref="ClassifyUniquePartialFilter" />): its key. Measured: it provides the order of a page sorted by
    ///         that key, the tiebreak would turn the page into a sort over every matching document, and the key is unique
    ///         within the types of the index anyway. A compound unique index does not qualify: only the combination of
    ///         its keys is unique, its first key can repeat. A UniqueNotDeleted index (partial filter on <c>rtState</c> as
    ///         well) does not provide the order of a page (the query's <c>rtState $ne</c> does not imply its filter), and
    ///         a unique index with any other partial filter does not cover every document of the page; neither
    ///         qualifies.</item>
    ///     </list>
    /// </summary>
    internal static string? LeadingKeyWithoutTiebreak(BsonDocument index)
    {
        if (!index.TryGetValue("name", out var nameValue) || !nameValue.IsString)
        {
            return null;
        }

        var match = CkIndexName().Match(nameValue.AsString);
        if (!match.Success || SystemIndexNumbers.Contains(match.Groups["number"].Value))
        {
            return null;
        }

        if (!index.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument key ||
            key.Names.Any(n => n is "_fts" or Constants.IdField))
        {
            return null;
        }

        if (index.TryGetValue("unique", out var unique) && unique.ToBoolean())
        {
            // Only a single-key unique index makes its key unique on its own (UniqueKeysWithoutTiebreak).
            return UniqueKeysWithoutTiebreak(index) is [var single] ? single.Path : null;
        }

        // The engine builds every CK Ascending index with ckTypeId first. An index without it (e.g. a hand-made one
        // whose default name `attributes.code_1` happens to match the name pattern) is not ours and gets the tiebreak.
        var names = key.Names.ToList();
        return names.Count > 1 && names[0] == CkTypeIdKey ? names[1] : null;
    }

    /// <summary>
    ///     For an index document of <c>listIndexes</c>: the keys (path, descending) of a CK Unique index, whose
    ///     combination is unique within its types, so a page sorted by all of them (in the index's directions or all
    ///     reversed) is totally ordered without the tiebreak (<see cref="PagingTiebreakGate" />). Null for every other
    ///     index: not named like a CK index, keys with <c>_id</c> or text, a partial filter other than the engine's type
    ///     filter (<see cref="ClassifyUniquePartialFilter" />: UniqueNotDeleted does not provide the page order, any other
    ///     predicate or none does not belong to a CK definition), or sparse, hidden or collated (a sparse index leaves out
    ///     the documents without the key, which then tie).
    /// </summary>
    internal static IReadOnlyList<(string Path, bool Descending)>? UniqueKeysWithoutTiebreak(BsonDocument index)
    {
        if (!index.TryGetValue("unique", out var unique) || !unique.ToBoolean() ||
            !index.TryGetValue("name", out var nameValue) || !nameValue.IsString ||
            CkIndexName().Match(nameValue.AsString) is not { Success: true } match ||
            SystemIndexNumbers.Contains(match.Groups["number"].Value) ||
            !index.TryGetValue("key", out var keyValue) || keyValue is not BsonDocument key || key.ElementCount == 0 ||
            key.Names.Any(n => n is "_fts" or Constants.IdField) || !key.Values.All(v => v.IsNumeric) ||
            index.Contains("collation") ||
            (index.TryGetValue("sparse", out var sparse) && sparse.ToBoolean()) ||
            (index.TryGetValue("hidden", out var hidden) && hidden.ToBoolean()) ||
            !index.TryGetValue("partialFilterExpression", out var partial) ||
            ClassifyUniquePartialFilter(partial) != UniquePartialFilter.Unique)
        {
            return null;
        }

        return key.Elements.Select(e => (e.Name, e.Value.ToDouble() < 0)).ToList();
    }

    /// <summary>True when a partial filter has a condition on <c>rtState</c> (top level or in an <c>$and</c>).</summary>
    internal static bool HasStateCondition(BsonValue? partialFilter) =>
        partialFilter is BsonDocument filter && Conditions(filter).Any(c => c.Name == RtStateKey);

    private static List<BsonElement> Conditions(BsonDocument filter)
    {
        var conditions = new List<BsonElement>();
        foreach (var element in filter)
        {
            if (element.Name == "$and" && element.Value is BsonArray parts && parts.All(p => p is BsonDocument))
            {
                conditions.AddRange(parts.SelectMany(p => p.AsBsonDocument.Elements));
            }
            else
            {
                conditions.Add(element);
            }
        }

        return conditions;
    }

    /// <summary>
    ///     What the partial filter of a unique index is (AB#8960): the engine builds a Unique index with
    ///     <c>{ckTypeId: {$in: [...]}}</c> and a UniqueNotDeleted index with that and <c>{rtState: {$in: [null, 0]}}</c>
    ///     (also accepted as the two conditions of an <c>$and</c>). Any other predicate, another <c>rtState</c> condition
    ///     or a filter without the type condition is <see cref="UniquePartialFilter.Foreign" />: such an index does not
    ///     enforce the uniqueness of a CK definition and is never treated as one.
    /// </summary>
    internal static UniquePartialFilter ClassifyUniquePartialFilter(BsonValue? partialFilter)
    {
        if (partialFilter is not BsonDocument filter)
        {
            return UniquePartialFilter.Foreign;
        }

        var conditions = Conditions(filter);
        var typeCondition = conditions.Where(c => c.Name == CkTypeIdKey).ToList();
        var stateCondition = conditions.Where(c => c.Name == RtStateKey).ToList();
        if (typeCondition.Count != 1 || stateCondition.Count > 1 ||
            conditions.Count != typeCondition.Count + stateCondition.Count ||
            !IsInCondition(typeCondition[0].Value, out _))
        {
            return UniquePartialFilter.Foreign;
        }

        if (stateCondition.Count == 0)
        {
            return UniquePartialFilter.Unique;
        }

        return IsInCondition(stateCondition[0].Value, out var states) && states.Count == 2 &&
               states.Count(v => v.IsBsonNull) == 1 && states.Count(v => v.IsNumeric && v.ToDouble() == 0) == 1
            ? UniquePartialFilter.UniqueNotDeleted
            : UniquePartialFilter.Foreign;
    }

    private static bool IsInCondition(BsonValue condition, out BsonArray values)
    {
        values = [];
        if (condition is BsonDocument document && document.ElementCount == 1 &&
            document.TryGetValue("$in", out var inValues) && inValues is BsonArray array)
        {
            values = array;
            return true;
        }

        return false;
    }

    // <Type>_<n>, also with the definition hash of an index built while another one held the name (<Type>_<n>_<hash>).
    [GeneratedRegex(@"^.+_(?<number>\d+)(?:_[0-9a-f]{6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CkIndexName();

    [GeneratedRegex(@"^_[0-9a-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex AlternateSuffix();
}

/// <summary>What the partial filter of a unique index is (<see cref="TiebreakIndexShape.ClassifyUniquePartialFilter" />).</summary>
internal enum UniquePartialFilter
{
    /// <summary>The type filter of a CK Unique index.</summary>
    Unique,

    /// <summary>The type and state filter of a CK UniqueNotDeleted index.</summary>
    UniqueNotDeleted,

    /// <summary>Any other filter: not the index of a CK definition.</summary>
    Foreign
}
