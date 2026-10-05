using System.Security.Cryptography;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Secrets;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     AB#5533: BSON storage of Secret attribute values. Only protected envelopes are written, as
///     <c>{ _t: "OctoSecret", e: "enc:v2:..." }</c>; pending and legacy values are refused, and the
///     sub-document reads back as <see cref="RtSecretValue.Protected" /> in any slot without CK
///     knowledge. The envelopes are structurally valid random bytes - no key, no plaintext.
/// </summary>
public class RtSecretValueSerializerTests
{
    private static readonly RtCkId<CkRecordId> CredentialRecordId = new("Test/CredentialEntry");

    static RtSecretValueSerializerTests()
    {
        MongoRepositoryClient.RegisterSerializers();
    }

    [Fact]
    public void Serialize_Protected_WritesTheOctoSecretSubDocument()
    {
        var envelope = NewEnvelope();

        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Protected(envelope)
        });

        var stored = attributes["apiKey"].AsBsonDocument;
        Assert.Equal(2, stored.ElementCount);
        Assert.Equal("OctoSecret", stored["_t"].AsString);
        Assert.Equal(envelope, stored["e"].AsString);
        Assert.True(RtSecretValueSerializer.IsStoredSecret(stored));
    }

    [Fact]
    public void Serialize_Pending_IsRefusedWithoutEchoingTheValue()
    {
        const string plaintext = "not-a-real-password-but-must-not-leak";

        var exception = Assert.Throws<SecretValueNotStorableException>(() =>
            SerializeAttributes(new Dictionary<string, object?> { ["ApiKey"] = RtSecretValue.Pending(plaintext) }));

        Assert.Equal(RtSecretValueState.Pending, exception.State);
        Assert.DoesNotContain(plaintext, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_LegacyPlaintext_IsRefused()
    {
        const string legacy = "legacy-plaintext-value";

        var exception = Assert.Throws<SecretValueNotStorableException>(() =>
            SerializeAttributes(new Dictionary<string, object?> { ["ApiKey"] = RtSecretValue.LegacyPlaintext(legacy) }));

        Assert.Equal(RtSecretValueState.LegacyPlaintext, exception.State);
        Assert.DoesNotContain(legacy, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_PendingInsideARecordArray_IsRefused()
    {
        var record = new RtRecord(CredentialRecordId, new Dictionary<string, object?>
        {
            ["Key"] = "smtp",
            ["Value"] = RtSecretValue.Pending("record-secret")
        });

        // Inside a class-mapped record the driver wraps the refusal in a BsonSerializationException
        // naming the member; the cause stays a SecretValueNotStorableException.
        var exception = Assert.ThrowsAny<Exception>(() =>
            SerializeAttributes(new Dictionary<string, object?> { ["Credentials"] = new List<RtRecord> { record } }));
        Assert.Contains(Chain(exception), e => e is SecretValueNotStorableException);
        Assert.All(Chain(exception), e => Assert.DoesNotContain("record-secret", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void RoundTrip_Protected_AtTopLevelAndInsideRecords()
    {
        var top = NewEnvelope();
        var inArray = NewEnvelope();
        var inRecord = NewEnvelope();

        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Protected(top),
            ["Credentials"] = new List<RtRecord>
            {
                new(CredentialRecordId, new Dictionary<string, object?>
                {
                    ["Key"] = "smtp", ["Value"] = RtSecretValue.Protected(inArray)
                })
            },
            ["PrimaryCredential"] = new RtRecord(CredentialRecordId, new Dictionary<string, object?>
            {
                ["Key"] = "main", ["Value"] = RtSecretValue.Protected(inRecord)
            })
        });

        // Record element names depend on the class-map conventions registered by the repository
        // client (covered end-to-end by SecretAttributeStorageTests); here only the secret shape is
        // pinned, wherever it sits.
        var stored = FindStoredSecrets(attributes).Select(d => d["e"].AsString).ToList();
        Assert.Equal(new[] { top, inArray, inRecord }.Order(), stored.Order());

        var read = DeserializeAttributes(new BsonDocument("apiKey", attributes["apiKey"]));
        var apiKey = Assert.IsType<RtSecretValue>(read["ApiKey"]);
        Assert.True(apiKey.IsProtected);
        Assert.Equal(top, apiKey.Envelope);
    }

    private static IEnumerable<BsonDocument> FindStoredSecrets(BsonValue value)
    {
        switch (value)
        {
            case BsonDocument document when RtSecretValueSerializer.IsStoredSecret(document):
                yield return document;
                break;
            case BsonDocument document:
                foreach (var element in document)
                {
                    foreach (var found in FindStoredSecrets(element.Value))
                    {
                        yield return found;
                    }
                }

                break;
            case BsonArray array:
                foreach (var item in array)
                {
                    foreach (var found in FindStoredSecrets(item))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    [Fact]
    public void Deserialize_SubDocumentInANonSecretSlot_DoesNotCrash()
    {
        // A slot whose CK type is not (or no longer) Secret still reads the sub-document as a
        // protected value instead of tripping the dynamic/discriminator path.
        var stored = new BsonDocument
        {
            { "description", new BsonDocument { { "_t", "OctoSecret" }, { "e", NewEnvelope() } } }
        };

        var read = DeserializeAttributes(stored);

        Assert.True(Assert.IsType<RtSecretValue>(read["Description"]).IsProtected);
    }

    [Fact]
    public void Deserialize_SubDocumentWithInvalidEnvelope_ReadsAsNotSet()
    {
        var stored = new BsonDocument
        {
            { "apiKey", new BsonDocument { { "_t", "OctoSecret" }, { "e", "enc:v2:k1:not base64!" } } },
            { "other", new BsonDocument { { "_t", "OctoSecret" } } }
        };

        var read = DeserializeAttributes(stored);

        Assert.Null(read["ApiKey"]);
        Assert.Null(read["Other"]);
    }

    [Fact]
    public void Deserialize_StringThroughTheSecretSerializer_IsLegacyPlaintext()
    {
        var serializer = BsonSerializer.LookupSerializer<RtSecretValue>();

        var legacy = Deserialize(serializer, new BsonDocument("v", "enc:v1:legacy"));
        var nothing = Deserialize(serializer, new BsonDocument("v", BsonNull.Value));

        Assert.True(legacy!.IsLegacyPlaintext);
        Assert.Null(nothing);
    }

    [Fact]
    public void Deserialize_StringInAttributeDictionary_StaysAString()
    {
        // Without CK knowledge a string is a string; the query layer (SecretAttributeReadNormalizer)
        // and GetAttributeSecretValueOrDefault turn it into LegacyPlaintext for Secret slots.
        var read = DeserializeAttributes(new BsonDocument { { "apiKey", "plain" } });

        Assert.Equal("plain", read["ApiKey"]);
    }

    [Fact]
    public void ToJson_OfTheRawDocument_NeverContainsAPlaintext()
    {
        // The stored form carries ciphertext only; this pins that the serializer does not add
        // anything else (e.g. a debug copy of the value).
        var envelope = NewEnvelope();
        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Protected(envelope)
        });

        var json = attributes.ToJson();
        Assert.Contains(SecretEnvelope.PrefixV2, json, StringComparison.Ordinal);
        Assert.DoesNotContain("***", json, StringComparison.Ordinal);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            yield return current;
        }
    }

    internal static string NewEnvelope()
    {
        var payload = RandomNumberGenerator.GetBytes(SecretEnvelope.NonceLength + SecretEnvelope.TagLength + 16);
        return SecretEnvelope.BuildHeaderV2("t1") + Convert.ToBase64String(payload)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static RtSecretValue? Deserialize(IBsonSerializer<RtSecretValue> serializer, BsonDocument wrapper)
    {
        using var reader = new BsonDocumentReader(wrapper);
        reader.ReadStartDocument();
        reader.ReadName();
        var value = serializer.Deserialize(BsonDeserializationContext.CreateRoot(reader));
        reader.ReadEndDocument();
        return value;
    }

    private static BsonDocument SerializeAttributes(Dictionary<string, object?> values)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        var context = BsonSerializationContext.CreateRoot(writer);

        writer.WriteStartDocument();
        writer.WriteName("attributes");
        new RtAttributeDictionarySerializer().Serialize(context,
            new BsonSerializationArgs { NominalType = typeof(Dictionary<string, object?>) }, values);
        writer.WriteEndDocument();

        return document["attributes"].AsBsonDocument;
    }

    private static Dictionary<string, object?> DeserializeAttributes(BsonDocument attributes)
    {
        var stored = new BsonDocument("attributes", attributes);
        using var reader = new BsonDocumentReader(stored);
        var context = BsonDeserializationContext.CreateRoot(reader);

        reader.ReadStartDocument();
        reader.ReadName();
        var result = new RtAttributeDictionarySerializer().Deserialize(context, new BsonDeserializationArgs());
        reader.ReadEndDocument();

        return result;
    }
}
