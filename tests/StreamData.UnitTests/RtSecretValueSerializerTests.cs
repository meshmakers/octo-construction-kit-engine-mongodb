using System.Security.Cryptography;

using Meshmakers.Octo.ConstructionKit.Contracts;
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
///     <c>{ _t: "OctoSecret", e: "enc:v2:...", t: ISODate }</c> (<c>t</c> = set-at, omitted when null); pending values are refused, legacy values are written back as their original string, and the
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
    public void Serialize_ProtectedWithSetAt_WritesTheTimestampAsBsonDate()
    {
        var envelope = NewEnvelope();
        var setAt = new DateTime(2026, 10, 6, 12, 34, 56, 789, DateTimeKind.Utc);

        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Protected(envelope, setAt)
        });

        var stored = attributes["apiKey"].AsBsonDocument;
        Assert.Equal(3, stored.ElementCount);
        Assert.Equal("OctoSecret", stored["_t"].AsString);
        Assert.Equal(envelope, stored["e"].AsString);
        Assert.Equal(BsonType.DateTime, stored["t"].BsonType);
        Assert.Equal(setAt, stored["t"].ToUniversalTime());
    }

    [Fact]
    public void RoundTrip_ProtectedWithSetAt_KeepsTheTimestamp()
    {
        var envelope = NewEnvelope();
        // BSON dates have millisecond precision: sub-millisecond ticks are dropped.
        var setAt = new DateTime(2026, 10, 6, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234);

        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Protected(envelope, setAt),
            ["PrimaryCredential"] = new RtRecord(CredentialRecordId, new Dictionary<string, object?>
            {
                ["Key"] = "main", ["Value"] = RtSecretValue.Protected(NewEnvelope(), setAt)
            })
        });

        Assert.All(FindStoredSecrets(attributes), d => Assert.True(d.Contains("t")));

        var read = DeserializeAttributes(new BsonDocument("apiKey", attributes["apiKey"]));
        var apiKey = Assert.IsType<RtSecretValue>(read["ApiKey"]);
        Assert.True(apiKey.IsProtected);
        Assert.Equal(envelope, apiKey.Envelope);
        Assert.Equal(new DateTime(2026, 10, 6, 12, 34, 56, 789, DateTimeKind.Utc), apiKey.SetAt);
        Assert.Equal(DateTimeKind.Utc, apiKey.SetAt!.Value.Kind);
    }

    [Fact]
    public void Serialize_ProtectedWithoutSetAt_OmitsTheTimestamp()
    {
        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Protected(NewEnvelope(), null)
        });

        Assert.False(attributes["apiKey"].AsBsonDocument.Contains("t"));
    }

    [Fact]
    public void Deserialize_LegacySubDocumentWithoutTimestamp_HasNoSetAt()
    {
        // Values stored before the timestamp existed ({ _t, e } only) read with SetAt = null; a t of
        // another BSON type is ignored (metadata only, never fails the read).
        var stored = new BsonDocument
        {
            { "apiKey", new BsonDocument { { "_t", "OctoSecret" }, { "e", NewEnvelope() } } },
            { "password", new BsonDocument { { "_t", "OctoSecret" }, { "e", NewEnvelope() }, { "t", "yesterday" } } }
        };

        var read = DeserializeAttributes(stored);

        var apiKey = Assert.IsType<RtSecretValue>(read["ApiKey"]);
        Assert.True(apiKey.IsProtected);
        Assert.Null(apiKey.SetAt);
        var password = Assert.IsType<RtSecretValue>(read["Password"]);
        Assert.True(password.IsProtected);
        Assert.Null(password.SetAt);
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
    public void Serialize_LegacyPlaintext_IsWrittenBackAsTheOriginalString()
    {
        // Saving an entity the encrypt sweep has not reached yet must keep its stored (legacy) form.
        const string legacy = "legacy-plaintext-value";
        const string legacyV1 = "enc:v1:oKGio6SlpqeoqaqrOxh7H702oGiyoSGkNg1vJ7bb2F42vG3NFkjx4iY=";

        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.LegacyPlaintext(legacy),
            ["Password"] = RtSecretValue.LegacyPlaintext(legacyV1),
            ["Credentials"] = new List<RtRecord>
            {
                new(CredentialRecordId, new Dictionary<string, object?>
                {
                    ["Key"] = "smtp",
                    ["Value"] = RtSecretValue.LegacyPlaintext(legacy)
                })
            }
        });

        Assert.Equal(new BsonString(legacy), attributes["apiKey"]);
        Assert.Equal(new BsonString(legacyV1), attributes["password"]);
        // Record element names depend on the registered class-map conventions; the legacy string must
        // sit in the element as a plain string, and no secret sub-document may have been written.
        Assert.Contains(new BsonString(legacy), AllValues(attributes["credentials"]));
        Assert.Empty(FindStoredSecrets(attributes));
    }

    private static IEnumerable<BsonValue> AllValues(BsonValue value)
    {
        yield return value;
        var children = value switch
        {
            BsonDocument document => document.Values,
            BsonArray array => array,
            _ => Enumerable.Empty<BsonValue>()
        };
        foreach (var child in children.SelectMany(AllValues))
        {
            yield return child;
        }
    }

    [Fact]
    public void LegacyPlaintext_RoundTripsThroughTheSecretSerializer()
    {
        const string legacy = "legacy-plaintext-value";
        var serializer = new RtSecretValueSerializer();
        var document = new BsonDocument();
        using (var writer = new BsonDocumentWriter(document))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            serializer.Serialize(BsonSerializationContext.CreateRoot(writer), RtSecretValue.LegacyPlaintext(legacy));
            writer.WriteEndDocument();
        }

        using var reader = new BsonDocumentReader(document);
        reader.ReadStartDocument();
        reader.ReadName("v");
        var read = serializer.Deserialize(BsonDeserializationContext.CreateRoot(reader));

        Assert.Equal(RtSecretValue.LegacyPlaintext(legacy), read);
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
