using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     AB#7445: builds the pieces of the ONE atomic MongoDB update that changes single items of RecordArray
///     attributes: the <c>$set</c> operations on <c>attributes.&lt;array&gt;.$[iN].attributes.&lt;member&gt;</c>, the
///     <c>arrayFilters</c> that select the item by its match member, and the document filter.
/// </summary>
/// <remarks>
///     Stored shape of a record in a RecordArray: <c>{ ckRecordId, attributes: { &lt;camelCaseMember&gt;: value } }</c>
///     (see <c>RtRecord</c> and <c>RtAttributeDictionarySerializer</c>). MongoDB refuses array updates through
///     <c>$[iN]</c> when the array path does not exist ("The path ... must exist in the document"), so the document
///     filter requires every referenced attribute to be an array; for an item-only update it additionally requires at
///     least one item to match, which is what makes "nothing matched" a no-op without a version bump.
/// </remarks>
internal sealed class RecordArrayItemUpdateStatement<TDocument>
{
    private RecordArrayItemUpdateStatement(IReadOnlyList<UpdateDefinition<TDocument>> sets,
        IReadOnlyList<ArrayFilterDefinition> arrayFilters, FilterDefinition<TDocument> arraysExistFilter,
        FilterDefinition<TDocument> anyItemMatchesFilter)
    {
        Sets = sets;
        ArrayFilters = arrayFilters;
        ArraysExistFilter = arraysExistFilter;
        AnyItemMatchesFilter = anyItemMatchesFilter;
    }

    /// <summary>The <c>$set</c> operations, one per member of every item update.</summary>
    public IReadOnlyList<UpdateDefinition<TDocument>> Sets { get; }

    /// <summary>One array filter per item update (identifier <c>i0</c>, <c>i1</c>, ...).</summary>
    public IReadOnlyList<ArrayFilterDefinition> ArrayFilters { get; }

    /// <summary>Every referenced attribute is an array in the stored document.</summary>
    public FilterDefinition<TDocument> ArraysExistFilter { get; }

    /// <summary>At least one item of at least one entry matches.</summary>
    public FilterDefinition<TDocument> AnyItemMatchesFilter { get; }

    public static RecordArrayItemUpdateStatement<TDocument> Create(IReadOnlyCollection<RtRecordArrayItemUpdate> itemUpdates)
    {
        var sets = new List<UpdateDefinition<TDocument>>();
        var arrayFilters = new List<ArrayFilterDefinition>();
        var arrayPaths = new SortedSet<string>(StringComparer.Ordinal);
        var itemMatches = new List<FilterDefinition<TDocument>>();

        var index = 0;
        foreach (var itemUpdate in itemUpdates)
        {
            var identifier = "i" + index++;
            var arrayPath = Constants.AttributesName + "." + itemUpdate.AttributeName.ToCamelCase();
            var matchField = Constants.AttributesName + "." + itemUpdate.MatchMember.ToCamelCase();
            var matchValue = BsonValue.Create(itemUpdate.MatchValue);

            arrayPaths.Add(arrayPath);
            arrayFilters.Add(new BsonDocumentArrayFilterDefinition<BsonDocument>(
                new BsonDocument(identifier + "." + matchField, matchValue)));
            itemMatches.Add(new BsonDocumentFilterDefinition<TDocument>(new BsonDocument(arrayPath,
                new BsonDocument("$elemMatch", new BsonDocument(matchField, matchValue)))));

            foreach (var (member, value) in itemUpdate.MemberUpdates)
            {
                sets.Add(Builders<TDocument>.Update.Set(
                    $"{arrayPath}.$[{identifier}].{Constants.AttributesName}.{member.ToCamelCase()}", value));
            }
        }

        var arraysExist = Builders<TDocument>.Filter.And(arrayPaths.Select(path =>
            (FilterDefinition<TDocument>)new BsonDocumentFilterDefinition<TDocument>(
                new BsonDocument(path, new BsonDocument("$type", "array")))));

        return new RecordArrayItemUpdateStatement<TDocument>(sets, arrayFilters, arraysExist,
            Builders<TDocument>.Filter.Or(itemMatches));
    }
}
