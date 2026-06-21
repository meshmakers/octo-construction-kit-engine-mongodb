using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// Behavior pinned for SlowQueryFingerprinter — the structural-hash function that lets the
// Studio surface group semantically-identical slow queries that differ only in literal
// values (AB#4213).
public sealed class SlowQueryFingerprinterTests
{
    [Fact]
    public void SameShape_DifferentLiterals_ProducesSameFingerprint()
    {
        var a = BsonDocument.Parse("""{ "find": "ck_types", "filter": { "name": "Asset" } }""");
        var b = BsonDocument.Parse("""{ "find": "ck_types", "filter": { "name": "Device" } }""");

        Assert.Equal(SlowQueryFingerprinter.Fingerprint(a), SlowQueryFingerprinter.Fingerprint(b));
    }

    [Fact]
    public void SameShape_DifferentValueTypes_StillSameFingerprint()
    {
        // All primitives are collapsed to the same placeholder; an int and a string in the
        // same slot are structurally indistinguishable for grouping purposes.
        var a = BsonDocument.Parse("""{ "find": "ck_types", "filter": { "x": 42 } }""");
        var b = BsonDocument.Parse("""{ "find": "ck_types", "filter": { "x": "forty-two" } }""");

        Assert.Equal(SlowQueryFingerprinter.Fingerprint(a), SlowQueryFingerprinter.Fingerprint(b));
    }

    [Fact]
    public void DifferentTopLevelCommand_ProducesDifferentFingerprint()
    {
        var find = BsonDocument.Parse("""{ "find": "ck_types", "filter": { } }""");
        var aggregate = BsonDocument.Parse("""{ "aggregate": "ck_types", "pipeline": [ ] }""");

        Assert.NotEqual(SlowQueryFingerprinter.Fingerprint(find),
            SlowQueryFingerprinter.Fingerprint(aggregate));
    }

    [Fact]
    public void DifferentFieldNames_ProduceDifferentFingerprint()
    {
        var a = BsonDocument.Parse("""{ "find": "x", "filter": { "name": "?" } }""");
        var b = BsonDocument.Parse("""{ "find": "x", "filter": { "title": "?" } }""");

        Assert.NotEqual(SlowQueryFingerprinter.Fingerprint(a), SlowQueryFingerprinter.Fingerprint(b));
    }

    [Fact]
    public void DifferentFieldOrder_ProducesDifferentFingerprint()
    {
        // MongoDB query semantics depend on field order for some operators; the fingerprint
        // must reflect that — {a, b} is not the same as {b, a}.
        var a = BsonDocument.Parse("""{ "find": "x", "filter": { "a": 1, "b": 2 } }""");
        var b = BsonDocument.Parse("""{ "find": "x", "filter": { "b": 2, "a": 1 } }""");

        Assert.NotEqual(SlowQueryFingerprinter.Fingerprint(a), SlowQueryFingerprinter.Fingerprint(b));
    }

    [Fact]
    public void PrimitiveArray_DifferentElementCount_SameFingerprint()
    {
        // $in/$nin/$all arrays: element count is data, not structure. Collapse to one placeholder.
        var a = BsonDocument.Parse("""{ "find": "x", "filter": { "tags": { "$in": [ "a", "b", "c" ] } } }""");
        var b = BsonDocument.Parse("""{ "find": "x", "filter": { "tags": { "$in": [ "z" ] } } }""");

        Assert.Equal(SlowQueryFingerprinter.Fingerprint(a), SlowQueryFingerprinter.Fingerprint(b));
    }

    [Fact]
    public void DocumentArray_DifferentLength_ProducesDifferentFingerprint()
    {
        // Aggregation pipelines: stage count and order are structurally significant.
        var one = BsonDocument.Parse("""{ "aggregate": "x", "pipeline": [ { "$match": { "a": 1 } } ] }""");
        var two = BsonDocument.Parse("""
            { "aggregate": "x", "pipeline": [ { "$match": { "a": 1 } }, { "$project": { "a": 1 } } ] }
            """);

        Assert.NotEqual(SlowQueryFingerprinter.Fingerprint(one), SlowQueryFingerprinter.Fingerprint(two));
    }

    [Fact]
    public void PipelineStages_DifferentOrder_ProducesDifferentFingerprint()
    {
        var matchFirst = BsonDocument.Parse("""
            { "aggregate": "x", "pipeline": [ { "$match": { "a": 1 } }, { "$project": { "b": 1 } } ] }
            """);
        var projectFirst = BsonDocument.Parse("""
            { "aggregate": "x", "pipeline": [ { "$project": { "b": 1 } }, { "$match": { "a": 1 } } ] }
            """);

        Assert.NotEqual(SlowQueryFingerprinter.Fingerprint(matchFirst),
            SlowQueryFingerprinter.Fingerprint(projectFirst));
    }

    [Fact]
    public void FingerprintIsStable_AcrossRepeatedCalls()
    {
        var doc = BsonDocument.Parse("""{ "find": "x", "filter": { "a": 1 } }""");

        var first = SlowQueryFingerprinter.Fingerprint(doc);
        var second = SlowQueryFingerprinter.Fingerprint(doc);
        var third = SlowQueryFingerprinter.Fingerprint(doc);

        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Fingerprint_IsExactly16HexChars()
    {
        var doc = BsonDocument.Parse("""{ "find": "x" }""");
        var fp = SlowQueryFingerprinter.Fingerprint(doc);

        Assert.Equal(SlowQueryFingerprinter.FingerprintLength, fp.Length);
        Assert.Matches("^[0-9a-f]{16}$", fp);
    }

    [Fact]
    public void Null_Or_EmptyDocument_ReturnsZeroFingerprint()
    {
        var zeros = new string('0', SlowQueryFingerprinter.FingerprintLength);
        Assert.Equal(zeros, SlowQueryFingerprinter.Fingerprint(null));
        Assert.Equal(zeros, SlowQueryFingerprinter.Fingerprint(new BsonDocument()));
    }

    [Fact]
    public void NestedDocuments_AreNormalisedRecursively()
    {
        var a = BsonDocument.Parse("""{ "find": "x", "filter": { "addr": { "city": "Vienna", "zip": 1010 } } }""");
        var b = BsonDocument.Parse("""{ "find": "x", "filter": { "addr": { "city": "Berlin", "zip": 10115 } } }""");

        Assert.Equal(SlowQueryFingerprinter.Fingerprint(a), SlowQueryFingerprinter.Fingerprint(b));
    }
}
