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
        // text index
        { "{ name: 'SimpleModelSimpleType_1', key: { _fts: 'text', _ftsx: 1 } }", null },
        // system-level indexes outside the CK definition
        { "{ name: 'RtEntity_SimpleModelSimpleType_9000', key: { rtState: 1 } }", null },
        { "{ name: 'RtEntity_SimpleModelSimpleType_9001', key: { ckTypeId: 1, rtCreatedBy: 1 } }", null },
        // the _id index and indexes not named by the engine
        { "{ name: '_id_', key: { _id: 1 } }", null },
        { "{ name: 'manual', key: { 'attributes.simpleField': 1 } }", null }
    };

    [Theory]
    [MemberData(nameof(Indexes))]
    public void LeadingKeyWithoutTiebreak_ForLegacyAscendingAndPlainUniqueIndexes(string index, string? expected)
    {
        Assert.Equal(expected, TiebreakIndexShape.LeadingKeyWithoutTiebreak(BsonDocument.Parse(index)));
    }
}
