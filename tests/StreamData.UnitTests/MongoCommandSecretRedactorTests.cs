using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// AB#5533: secret envelopes must never appear in rendered command diagnostics.
public sealed class MongoCommandSecretRedactorTests
{
    // Obviously fake envelope (structure only, no real key material).
    private const string FakeEnvelope = "enc:v2:testkid:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FakeV1Envelope = "enc:v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static BsonDocument Secret(string envelope = FakeEnvelope) =>
        new() { { "_t", "OctoSecret" }, { "e", envelope } };

    [Fact]
    public void Redact_NoSecret_ReturnsSameInstance()
    {
        var command = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument("attributes.name", "x") }
        };

        Assert.Same(command, MongoCommandSecretRedactor.Redact(command));
    }

    [Fact]
    public void Redact_SecretSubDocument_ReplacedEverywhere_InputUntouched()
    {
        var command = new BsonDocument
        {
            { "update", "rt_entities" },
            {
                "updates", new BsonArray
                {
                    new BsonDocument
                    {
                        { "q", new BsonDocument("_id", 1) },
                        {
                            "u", new BsonDocument("$set", new BsonDocument
                            {
                                { "attributes.password", Secret() },
                                { "attributes.name", "visible" },
                                { "attributes.records", new BsonArray { new BsonDocument("token", Secret()) } }
                            })
                        }
                    }
                }
            },
            { "ordered", true }
        };
        var before = command.ToJson();

        var redacted = MongoCommandSecretRedactor.Redact(command);
        var json = redacted.ToJson();

        Assert.DoesNotContain(FakeEnvelope, json);
        Assert.Contains("\"visible\"", json);
        Assert.Contains("\"ordered\" : true", json);
        Assert.Equal(2, CountOccurrences(json, "\"e\" : \"***\""));
        Assert.Equal(2, CountOccurrences(json, "OctoSecret"));
        // Field order is preserved and the input is not mutated.
        Assert.Equal(new[] { "update", "updates", "ordered" }, redacted.Names.ToArray());
        Assert.Equal(before, command.ToJson());
    }

    [Fact]
    public void Redact_SecretSubDocument_WithDiscriminatorNotFirst_Redacted()
    {
        var command = new BsonDocument
        {
            { "insert", "rt_entities" },
            { "documents", new BsonArray { new BsonDocument("attributes", new BsonDocument("pw",
                new BsonDocument { { "e", FakeEnvelope }, { "_t", "OctoSecret" } })) } }
        };

        var json = MongoCommandSecretRedactor.ToRedactedJson(command);

        Assert.DoesNotContain(FakeEnvelope, json);
        Assert.Contains("***", json);
    }

    [Fact]
    public void Redact_EnvelopeStrings_V1AndV2_Redacted()
    {
        var command = new BsonDocument
        {
            { "update", "rt_entities" },
            { "u", new BsonDocument("$set", new BsonDocument
                {
                    { "attributes.password.e", FakeEnvelope },
                    { "attributes.legacy", FakeV1Envelope },
                    { "attributes.other", "enc:not-an-envelope" }
                })
            }
        };

        var json = MongoCommandSecretRedactor.ToRedactedJson(command);

        Assert.DoesNotContain(FakeEnvelope, json);
        Assert.DoesNotContain(FakeV1Envelope, json);
        Assert.Contains("enc:not-an-envelope", json);
    }

    [Fact]
    public void Redact_RawBsonDocument_Redacted()
    {
        var command = new BsonDocument
        {
            { "findAndModify", "rt_entities" },
            { "update", new BsonDocument("$set", new BsonDocument("attributes.pw", Secret())) }
        };
        using var raw = new RawBsonDocument(command.ToBson());

        var json = MongoCommandSecretRedactor.ToRedactedJson(raw);

        Assert.DoesNotContain(FakeEnvelope, json);
        Assert.Contains("\"e\" : \"***\"", json);
    }

    [Fact]
    public void ToRedactedJson_NoSecret_EqualsPlainJson()
    {
        var command = new BsonDocument { { "find", "rt_entities" }, { "limit", 5 } };

        Assert.Equal(command.ToJson(), MongoCommandSecretRedactor.ToRedactedJson(command));
    }

    [Fact]
    public void ToRedactedJsonOrPlaceholder_DisposedRawDocument_ReturnsPlaceholder_NeverThrows()
    {
        var raw = new RawBsonDocument(new BsonDocument("insert", "x").ToBson());
        raw.Dispose();

        Assert.Equal(MongoCommandSecretRedactor.DroppedPreview,
            MongoCommandSecretRedactor.ToRedactedJsonOrPlaceholder(raw));
        Assert.Equal("<null>", MongoCommandSecretRedactor.ToRedactedJsonOrPlaceholder(null));
    }

    [Fact]
    public void TruncateBson_RedactsBeforeTruncation()
    {
        var command = new BsonDocument
        {
            { "findAndModify", "rt_entities" },
            { "update", new BsonDocument("$set", new BsonDocument("attributes.pw", Secret())) }
        };

        var full = MongoCommandObservability.TruncateBson(command, maxBytes: 4096);
        var truncated = MongoCommandObservability.TruncateBson(command, maxBytes: 90);

        Assert.DoesNotContain(FakeEnvelope, full);
        Assert.Contains("\"e\" : \"***\"", full);
        // A truncation cut must not leave a prefix of the envelope behind either.
        Assert.DoesNotContain("enc:v2", truncated);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
