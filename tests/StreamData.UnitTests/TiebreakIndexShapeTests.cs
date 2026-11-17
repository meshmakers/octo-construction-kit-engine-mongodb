using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     The tiebreak shape of CK Ascending indexes (AB#8960): where <c>_id</c> goes, and which indexes of
///     <c>listIndexes</c> are legacy and for which sort key the paging gate keeps their order.
/// </summary>
public class TiebreakIndexShapeTests
{
    [Fact]
    public void WithIdTiebreak_PutsIdBeforeTheAppendedRtState()
    {
        var keys = TiebreakIndexShape.WithIdTiebreak(["ckTypeId", "attributes.name", "rtState"]);

        Assert.Equal(["ckTypeId", "attributes.name", "_id", "rtState"], keys);
    }

    [Fact]
    public void WithIdTiebreak_PutsIdBeforeAnRtStateTheCkDefinitionListsItself()
    {
        // A key after the $ne range on rtState could not provide the order, so _id goes before it.
        var keys = TiebreakIndexShape.WithIdTiebreak(["ckTypeId", "attributes.name", "rtState", "attributes.code"]);

        Assert.Equal(["ckTypeId", "attributes.name", "_id", "rtState", "attributes.code"], keys);
    }

    [Fact]
    public void WithIdTiebreak_AppendsIdWithoutRtState()
    {
        Assert.Equal(["attributes.name", "_id"], TiebreakIndexShape.WithIdTiebreak(["attributes.name"]));
    }

    [Fact]
    public void WithIdTiebreak_LeavesAnIndexThatAlreadyHoldsTheId()
    {
        // The System Entity index {CkTypeId, _id} is in the tiebreak shape already.
        Assert.Null(TiebreakIndexShape.WithIdTiebreak(["ckTypeId", "_id", "rtState"]));
    }

    public static TheoryData<string, string?> Indexes() => new()
    {
        // CK Ascending index, legacy shape: it provides the order of a page sorted by its attribute
        { "{ name: 'SimpleModelSimpleType_0', key: { ckTypeId: 1, 'attributes.simpleField': 1, rtState: 1 } }", "attributes.simpleField" },
        // System Entity index on RtWellKnownName, legacy shape
        { "{ name: 'SystemEntity_0', key: { ckTypeId: 1, rtWellKnownName: 1, rtState: 1 } }", "rtWellKnownName" },
        // the same index in the tiebreak shape
        { "{ name: 'SimpleModelSimpleType_0_id', key: { ckTypeId: 1, 'attributes.simpleField': 1, _id: 1, rtState: 1 } }", null },
        // System Entity {CkTypeId, _id}: already holds _id
        { "{ name: 'SystemEntity_1', key: { ckTypeId: 1, _id: 1, rtState: 1 } }", null },
        // a plain Unique index serves the order of its first key (measured); UniqueNotDeleted (partial on rtState) does not
        { "{ name: 'SimpleModelSimpleType_0', key: { 'attributes.simpleField': 1 }, unique: true, partialFilterExpression: { ckTypeId: { $in: ['SimpleModel/SimpleType'] } } }", "attributes.simpleField" },
        { "{ name: 'SimpleModelSimpleType_0', key: { 'attributes.simpleField': 1 }, unique: true, partialFilterExpression: { rtState: { $in: [null, 0] } } }", null },
        { "{ name: 'SimpleModelSimpleType_0', key: { 'attributes.simpleField': 1 }, unique: true, partialFilterExpression: { rtState: { $in: [null, 0] }, ckTypeId: { $in: ['SimpleModel/SimpleType'] } } }", null },
        // a compound unique index is unique only in the combination of its keys: its first key can repeat
        { "{ name: 'SimpleModelSimpleType_0', key: { 'attributes.simpleField': 1, 'attributes.otherField': 1 }, unique: true, partialFilterExpression: { ckTypeId: { $in: ['SimpleModel/SimpleType'] } } }", null },
        // a unique index with a further predicate keeps only part of the documents unique
        { "{ name: 'SimpleModelSimpleType_0', key: { 'attributes.simpleField': 1 }, unique: true, partialFilterExpression: { ckTypeId: { $in: ['SimpleModel/SimpleType'] }, 'attributes.enabled': true } }", null },
        // the same indexes under the alternate name of an update of their slot (AB#8960, AC 8)
        { "{ name: 'SimpleModelSimpleType_0_a1b2c3', key: { 'attributes.simpleField': 1 }, unique: true, partialFilterExpression: { ckTypeId: { $in: ['SimpleModel/SimpleType'] } } }", "attributes.simpleField" },
        { "{ name: 'SimpleModelSimpleType_0_a1b2c3', key: { ckTypeId: 1, 'attributes.simpleField': 1, rtState: 1 } }", "attributes.simpleField" },
        { "{ name: 'SimpleModelSimpleType_0_id_a1b2c3', key: { ckTypeId: 1, 'attributes.simpleField': 1, _id: 1, rtState: 1 } }", null },
        // text index
        { "{ name: 'SimpleModelSimpleType_1', key: { _fts: 'text', _ftsx: 1 } }", null },
        // system-level indexes outside the CK definition
        { "{ name: 'RtEntity_SimpleModelSimpleType_9000', key: { rtState: 1 } }", null },
        { "{ name: 'RtEntity_SimpleModelSimpleType_9001', key: { ckTypeId: 1, rtCreatedBy: 1 } }", null },
        // the _id index and indexes not named by the engine
        { "{ name: '_id_', key: { _id: 1 } }", null },
        { "{ name: 'manual', key: { 'attributes.simpleField': 1 } }", null },
        // a hand-made index whose default name matches the CK name pattern, without the leading ckTypeId
        { "{ name: 'attributes.simpleField_1', key: { 'attributes.simpleField': 1 } }", null },
        { "{ name: 'attributes.simpleField_1_rtState_1', key: { 'attributes.simpleField': 1, rtState: 1 } }", null }
    };

    [Fact]
    public void AlternateIndexName_IsDeterministicPerDefinition()
    {
        static CkTypeIndex Index(params string[] keys) => new()
        {
            IndexType = IndexTypes.Ascending,
            Fields = [new CkIndexFields { AttributeNames = keys.ToList() }]
        };

        var name = MongoDbRepositoryDataSource.AlternateIndexName("T_0_id", Index("ckTypeId", "attributes.a", "_id"));

        Assert.Matches("^T_0_id_[0-9a-f]{6}$", name);
        Assert.Equal(name, MongoDbRepositoryDataSource.AlternateIndexName("T_0_id", Index("ckTypeId", "attributes.a", "_id")));
        Assert.NotEqual(name, MongoDbRepositoryDataSource.AlternateIndexName("T_0_id", Index("ckTypeId", "attributes.b", "_id")));
    }

    public static TheoryData<string, string> UniqueFilters() => new()
    {
        // the engine's filters (CreateUniqueIndexAsync, CreateUniqueNotDeletedIndexAsync)
        { "{ ckTypeId: { $in: ['M/A', 'M/B'] } }", nameof(UniquePartialFilter.Unique) },
        { "{ rtState: { $in: [null, 0] }, ckTypeId: { $in: ['M/A'] } }", nameof(UniquePartialFilter.UniqueNotDeleted) },
        { "{ $and: [ { rtState: { $in: [0, null] } }, { ckTypeId: { $in: ['M/A'] } } ] }", nameof(UniquePartialFilter.UniqueNotDeleted) },
        // anything else
        { "{ ckTypeId: { $in: ['M/A'] }, 'attributes.enabled': true }", nameof(UniquePartialFilter.Foreign) },
        { "{ rtState: { $in: [null, 0, 1] }, ckTypeId: { $in: ['M/A'] } }", nameof(UniquePartialFilter.Foreign) },
        { "{ rtState: 0, ckTypeId: { $in: ['M/A'] } }", nameof(UniquePartialFilter.Foreign) },
        { "{ rtState: { $in: [null, 0] } }", nameof(UniquePartialFilter.Foreign) },
        { "{ ckTypeId: 'M/A' }", nameof(UniquePartialFilter.Foreign) },
        { "{ ckTypeId: { $in: ['M/A'], $exists: true } }", nameof(UniquePartialFilter.Foreign) },
        { "{ }", nameof(UniquePartialFilter.Foreign) }
    };

    [Fact]
    public void UniqueKeysWithoutTiebreak_ForCkUniqueIndexesOnly()
    {
        const string filter = "partialFilterExpression: { ckTypeId: { $in: ['M/A'] } }";
        static IReadOnlyList<(string Path, bool Descending)>? Keys(string index) =>
            TiebreakIndexShape.UniqueKeysWithoutTiebreak(BsonDocument.Parse(index));

        Assert.Equal([("attributes.a", false), ("attributes.b", false)],
            Keys($"{{ name: 'MA_0', key: {{ 'attributes.a': 1, 'attributes.b': 1 }}, unique: true, {filter} }}"));
        // sparse, hidden or collated: documents without the key or with collated ties are not ordered by it
        Assert.Null(Keys($"{{ name: 'MA_0', key: {{ 'attributes.a': 1 }}, unique: true, sparse: true, {filter} }}"));
        Assert.Null(Keys($"{{ name: 'MA_0', key: {{ 'attributes.a': 1 }}, unique: true, hidden: true, {filter} }}"));
        Assert.Null(Keys($"{{ name: 'MA_0', key: {{ 'attributes.a': 1 }}, unique: true, collation: {{ locale: 'de' }}, {filter} }}"));
        // no partial filter: the engine never builds a unique index without its type filter
        Assert.Null(Keys("{ name: 'MA_0', key: { 'attributes.a': 1 }, unique: true }"));
        // not unique, not named like a CK index
        Assert.Null(Keys($"{{ name: 'MA_0', key: {{ 'attributes.a': 1 }}, {filter} }}"));
        Assert.Null(Keys($"{{ name: 'manual', key: {{ 'attributes.a': 1 }}, unique: true, {filter} }}"));
    }

    [Fact]
    public void HasStateCondition_LooksForTheCondition_NotForTheText()
    {
        // A type id containing "rtState" (Model/StartState) is not a state condition.
        Assert.False(TiebreakIndexShape.HasStateCondition(BsonDocument.Parse("{ ckTypeId: { $in: ['Model/StartState'] } }")));
        Assert.True(TiebreakIndexShape.HasStateCondition(
            BsonDocument.Parse("{ rtState: { $in: [null, 0] }, ckTypeId: { $in: ['Model/StartState'] } }")));
        Assert.True(TiebreakIndexShape.HasStateCondition(
            BsonDocument.Parse("{ $and: [ { rtState: { $in: [null, 0] } }, { ckTypeId: { $in: ['M/A'] } } ] }")));
    }

    [Fact]
    public void IsAlternateNameOf_RecognisesTheNamesAlternateIndexNameBuilds()
    {
        var alternate = TiebreakIndexShape.AlternateName("T_0_id", "a1b2c3d4e5");

        Assert.Equal("T_0_id_a1b2c3", alternate);
        Assert.True(TiebreakIndexShape.IsAlternateNameOf(alternate, "T_0_id"));
        Assert.False(TiebreakIndexShape.IsAlternateNameOf("T_0_id", "T_0"));
        Assert.False(TiebreakIndexShape.IsAlternateNameOf("T_0_id_A1B2C3", "T_0_id"));
        Assert.False(TiebreakIndexShape.IsAlternateNameOf("T_0_id_a1b2c3d", "T_0_id"));
    }

    [Theory]
    [MemberData(nameof(UniqueFilters))]
    public void ClassifyUniquePartialFilter_AcceptsOnlyTheEngineFilters(string filter, string expected)
    {
        Assert.Equal(expected, TiebreakIndexShape.ClassifyUniquePartialFilter(BsonDocument.Parse(filter)).ToString());
    }

    [Theory]
    [MemberData(nameof(Indexes))]
    public void LeadingKeyWithoutTiebreak_ForLegacyAscendingAndSingleKeyUniqueIndexes(string index, string? expected)
    {
        Assert.Equal(expected, TiebreakIndexShape.LeadingKeyWithoutTiebreak(BsonDocument.Parse(index)));
    }
}
