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
    ///         <item>a Unique index (partial filter on <c>ckTypeId</c> only): its first key. Measured: it provides the
    ///         order of a page sorted by that key, the tiebreak would turn the page into a sort over every matching
    ///         document, and the key is unique within the types of the index anyway. A UniqueNotDeleted index (partial
    ///         filter on <c>rtState</c> as well) does not provide the order of a page (the query's <c>rtState $ne</c> does
    ///         not imply its filter) and does not qualify.</item>
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
            var partialOnRtState = index.TryGetValue("partialFilterExpression", out var partial) &&
                                   partial.ToJson().Contains(RtStateKey, StringComparison.Ordinal);
            return partialOnRtState ? null : key.Names.FirstOrDefault();
        }

        // The engine builds every CK Ascending index with ckTypeId first. An index without it (e.g. a hand-made one
        // whose default name `attributes.code_1` happens to match the name pattern) is not ours and gets the tiebreak.
        var names = key.Names.ToList();
        return names.Count > 1 && names[0] == CkTypeIdKey ? names[1] : null;
    }

    // <Type>_<n>, also with the definition hash of an index built while another one held the name (<Type>_<n>_<hash>).
    [GeneratedRegex(@"^.+_(?<number>\d+)(?:_[0-9a-f]{6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CkIndexName();
}
