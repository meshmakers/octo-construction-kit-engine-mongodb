using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     The index a sorted page is hinted to (AB#9138): only an index that provides the sort after the <c>ckTypeId</c>
///     equality, never one that could return fewer documents or that the query cannot use.
/// </summary>
public class SortIndexHintTests
{
    private static readonly (string, bool)[] ById = [("_id", false)];
    private static readonly (string, bool)[] ByName = [("attributes.name", false)];
    private static readonly (string, bool)[] ByNameDescending = [("attributes.name", true)];
    private static readonly (string, bool)[] ByNameThenId = [("attributes.name", false), ("_id", false)];
    private static readonly (string, bool)[] ByNameDescThenIdDesc = [("attributes.name", true), ("_id", true)];
    private static readonly (string, bool)[] ByNameThenIdDesc = [("attributes.name", false), ("_id", true)];

    private static BsonDocument Index(string json) => BsonDocument.Parse(json);

    public static TheoryData<string, (string, bool)[], bool> Cases() => new()
    {
        // System Entity {CkTypeId, _id} (+ rtState) serves a sort by rtId
        { "{ name: 'SystemEntity_1', key: { ckTypeId: 1, _id: 1, rtState: 1 } }", ById, true },
        // legacy CK Ascending shape serves a sort by the attribute, forwards and backwards
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 } }", ByName, true },
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 } }", ByNameDescending, true },
        // AB#8960 tiebreak shape serves attribute + _id, forwards and backwards
        { "{ name: 'T_0_id', key: { ckTypeId: 1, 'attributes.name': 1, _id: 1, rtState: 1 } }", ByNameThenId, true },
        { "{ name: 'T_0_id', key: { ckTypeId: 1, 'attributes.name': 1, _id: 1, rtState: 1 } }", ByNameDescThenIdDesc, true },
        // ... but not mixed directions, and the legacy shape not attribute + _id
        { "{ name: 'T_0_id', key: { ckTypeId: 1, 'attributes.name': 1, _id: 1, rtState: 1 } }", ByNameThenIdDesc, false },
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 } }", ByNameThenId, false },
        // a sort key after an unrelated key, or without the ckTypeId prefix
        { "{ name: 'T_1', key: { ckTypeId: 1, 'attributes.code': 1, 'attributes.name': 1 } }", ByName, false },
        { "{ name: '_id_', key: { _id: 1 } }", ById, false },
        { "{ name: 'T_2', key: { 'attributes.name': 1 } }", ByName, false },
        // indexes that may hold fewer documents than the query, or that the query cannot use
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1 }, partialFilterExpression: { rtState: 0 } }", ByName, false },
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1 }, sparse: true }", ByName, false },
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1 }, hidden: true }", ByName, false },
        { "{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1 }, collation: { locale: 'de' } }", ByName, false },
        { "{ name: 'T_t', key: { ckTypeId: 1, _fts: 'text', _ftsx: 1 } }", ByName, false },
        { "{ name: 'T_h', key: { ckTypeId: 1, 'attributes.name': 'hashed' } }", ByName, false },
        { "{ name: 'T_g', key: { ckTypeId: 1, 'attributes.name': '2dsphere' } }", ByName, false },
        // special keys after the sort keys leave documents out of the index or cannot serve the query
        { "{ name: 'T_g2', key: { ckTypeId: 1, 'attributes.name': 1, 'attributes.location': '2dsphere' } }", ByName, false },
        { "{ name: 'T_t2', key: { ckTypeId: 1, 'attributes.name': 1, _fts: 'text', _ftsx: 1 } }", ByName, false },
        { "{ name: 'T_w', key: { ckTypeId: 1, 'attributes.name': 1, 'attributes.$**': 1 } }", ByName, false }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Provides_OnlyAnIndexThatYieldsTheSortAfterTheTypeEquality(string index, (string, bool)[] sort,
        bool expected)
    {
        Assert.Equal(expected, SortIndexHint.Provides(Index(index), sort));
    }

    [Fact]
    public void Choose_TakesTheFirstProvidingIndex_AndNothingWithoutOne()
    {
        var indexes = new[]
        {
            Index("{ name: '_id_', key: { _id: 1 } }"),
            Index("{ name: 'T_9001', key: { ckTypeId: 1, rtCreatedBy: 1 } }"),
            Index("{ name: 'SystemEntity_1', key: { ckTypeId: 1, _id: 1, rtState: 1 } }"),
            Index("{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 } }")
        };

        Assert.Equal("SystemEntity_1", SortIndexHint.Choose(indexes, ById)?.Name);
        Assert.Equal(BsonDocument.Parse("{ ckTypeId: 1, _id: 1, rtState: 1 }"), SortIndexHint.Choose(indexes, ById)?.Key);
        Assert.Equal("T_0", SortIndexHint.Choose(indexes, ByName)?.Name);
        Assert.Null(SortIndexHint.Choose(indexes, [("attributes.code", false)]));
        Assert.Null(SortIndexHint.Choose(indexes, []));
        Assert.Null(SortIndexHint.Choose(null, ById));
    }

    [Fact]
    public void Choose_SkipsAnIndexWhoseKeyPatternAnotherIndexShares()
    {
        // MongoDB refuses a key-pattern hint that matches two indexes; the collated twin itself never qualifies.
        var indexes = new[]
        {
            Index("{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 } }"),
            Index("{ name: 'T_0_de', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 }, collation: { locale: 'de' } }"),
            Index("{ name: 'T_0_id', key: { ckTypeId: 1, 'attributes.name': 1, _id: 1, rtState: 1 } }")
        };

        // A later index that provides the sort and has a key pattern of its own is taken instead.
        Assert.Equal("T_0_id", SortIndexHint.Choose(indexes, ByName)?.Name);
        Assert.Null(SortIndexHint.Choose(indexes.Take(2).ToArray(), ByName));
    }

    [Fact]
    public void FilterFields_CollectsTheFieldsThroughAndOrNor()
    {
        var filter = BsonDocument.Parse(
            "{ $or: [ { ckTypeId: { $nin: ['a'] } }, { ckTypeId: { $in: ['a'] } } ], ckTypeId: { $in: ['t'] }, " +
            "rtState: { $ne: 1 }, $and: [ { rtCreationDateTime: { $gte: 1 } }, { $nor: [ { 'attributes.code': 'x' } ] } ], " +
            "$expr: { $gt: ['$a', 1] } }");

        Assert.Equal(new[] { "attributes.code", "ckTypeId", "rtCreationDateTime", "rtState" },
            SortIndexHint.FilterFields(filter).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HasFilterIndex_OnlyForAnIndexLeadingWithAFilteredFieldOtherThanTypeStateAndTheSortKey()
    {
        var indexes = new[]
        {
            Index("{ name: '_id_', key: { _id: 1 } }"),
            Index("{ name: 'SystemEntity_1', key: { ckTypeId: 1, _id: 1, rtState: 1 } }"),
            Index("{ name: 'T_9000', key: { rtState: 1 } }"),
            Index("{ name: 'T_9001', key: { ckTypeId: 1, rtCreatedBy: 1 } }"),
            Index("{ name: 'T_0', key: { ckTypeId: 1, 'attributes.name': 1, rtState: 1 } }"),
            Index("{ name: 'T_1', key: { ckTypeId: 1, rtCreationDateTime: 1, rtState: 1 } }")
        };

        // type, state and an unindexed attribute: nothing competes with the sort index
        Assert.False(SortIndexHint.HasFilterIndex(indexes,
            new HashSet<string> { "ckTypeId", "rtState", "attributes.processed" }, "_id"));
        // a filter on the sort key itself: the sort index serves both
        Assert.False(SortIndexHint.HasFilterIndex(indexes,
            new HashSet<string> { "ckTypeId", "attributes.name" }, "attributes.name"));
        // indexed filter fields compete: a range on rtCreationDateTime, OwnedOnly on rtCreatedBy, an rtId list
        Assert.True(SortIndexHint.HasFilterIndex(indexes,
            new HashSet<string> { "ckTypeId", "rtCreationDateTime" }, "_id"));
        Assert.True(SortIndexHint.HasFilterIndex(indexes, new HashSet<string> { "ckTypeId", "rtCreatedBy" }, "_id"));
        Assert.True(SortIndexHint.HasFilterIndex(indexes,
            new HashSet<string> { "ckTypeId", "_id" }, "attributes.name"));
    }
}
