using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// AB#8009: the values of Hidden / securitySensitive / Secret attributes and of the known credential fields
// never appear in a rendered MongoDB command. All values below are fixed dummy constants.
public sealed class MongoCommandSensitiveAttributeRedactionTests
{
    internal const string DummyPasswordHash = "DUMMY-PASSWORD-HASH-8009";
    internal const string DummySecurityStamp = "DUMMY-SECURITY-STAMP-8009";
    internal const string DummyAuthenticatorKey = "DUMMY-AUTHENTICATOR-KEY-8009";
    internal const string DummyClientSecretHash = "DUMMY-CLIENT-SECRET-HASH-8009";
    private const string DummyPin = "DUMMY-PIN-8009";
    private const string FakeEnvelope = "enc:v2:testkid:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static readonly string[] AllDummies =
        [DummyPasswordHash, DummySecurityStamp, DummyAuthenticatorKey, DummyClientSecretHash];

    /// <summary>The insert of a System.Identity user as the engine writes it (bulk payload).</summary>
    internal static BsonDocument UserInsert() => new()
    {
        { "insert", "System_Identity_User" },
        { "ordered", true },
        {
            "documents", new BsonArray
            {
                new BsonDocument
                {
                    { "_id", "65a000000000000000000001" },
                    { "ckTypeId", "System.Identity/User" },
                    {
                        "attributes", new BsonDocument
                        {
                            { "userName", "dummy.user" },
                            { "emailConfirmed", true },
                            { "passwordHash", DummyPasswordHash },
                            { "securityStamp", DummySecurityStamp },
                            {
                                "userTokens", new BsonArray
                                {
                                    new BsonDocument
                                    {
                                        { "ckRecordId", "System.Identity/UserToken" },
                                        {
                                            "attributes", new BsonDocument
                                            {
                                                { "loginProvider", "[AspNetUserStore]" },
                                                { "name", "AuthenticatorKey" },
                                                { "value", DummyAuthenticatorKey }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    /// <summary>A client secret write (AddSecret) and the lookup by value (RemoveSecret).</summary>
    internal static BsonDocument ClientSecretUpdate() => new()
    {
        { "findAndModify", "System_Identity_Client" },
        {
            "query", new BsonDocument
            {
                { "_id", "65a000000000000000000002" },
                { "attributes.clientSecrets.attributes.value", DummyClientSecretHash }
            }
        },
        {
            "update", new BsonDocument("$set", new BsonDocument
            {
                {
                    "attributes.clientSecrets", new BsonArray
                    {
                        new BsonDocument
                        {
                            { "ckRecordId", "System.Identity/Secret" },
                            {
                                "attributes", new BsonDocument
                                {
                                    { "description", "dummy secret" },
                                    { "type", "SharedSecret" },
                                    { "value", DummyClientSecretHash }
                                }
                            }
                        }
                    }
                },
                { "rtChangedDateTime", new BsonDateTime(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)) }
            })
        }
    };

    [Fact]
    public void UserInsert_KnownCredentialFields_AreMasked_FieldNamesAndOtherValuesStay()
    {
        var json = MongoCommandSecretRedactor.ToRedactedJson(UserInsert());

        AssertNoDummy(json);
        Assert.Contains("\"passwordHash\" : \"***\"", json);
        Assert.Contains("\"securityStamp\" : \"***\"", json);
        // Every level below a sensitive attribute is masked, the shape stays.
        Assert.Contains("\"loginProvider\" : \"***\"", json);
        Assert.Contains("\"value\" : \"***\"", json);
        Assert.Contains("\"userName\" : \"dummy.user\"", json);
        Assert.Contains("\"emailConfirmed\" : true", json);
        Assert.Contains("System_Identity_User", json);
    }

    [Fact]
    public void ClientSecretWrite_SetAndLookupByValue_AreMasked()
    {
        var json = MongoCommandSecretRedactor.ToRedactedJson(ClientSecretUpdate());

        AssertNoDummy(json);
        Assert.Contains("\"attributes.clientSecrets.attributes.value\" : \"***\"", json);
        Assert.Contains("\"description\" : \"***\"", json);
        Assert.Contains("\"_id\" : \"65a000000000000000000002\"", json);
        Assert.Contains("rtChangedDateTime", json);
    }

    [Fact]
    public void DottedPaths_ItemUpdateAndWholeAttributeMap_AreMasked()
    {
        var command = new BsonDocument
        {
            { "update", "System_Identity_User" },
            {
                "updates", new BsonArray
                {
                    new BsonDocument
                    {
                        { "q", new BsonDocument("_id", 1) },
                        {
                            "u", new BsonDocument("$set", new BsonDocument
                            {
                                { "attributes.userTokens.$[i0].attributes.value", DummyAuthenticatorKey },
                                { "attributes.securityStamp", DummySecurityStamp }
                            })
                        },
                        { "arrayFilters", new BsonArray { new BsonDocument("i0.attributes.name", "AuthenticatorKey") } }
                    },
                    new BsonDocument
                    {
                        { "q", new BsonDocument("_id", 2) },
                        { "u", new BsonDocument("$set", new BsonDocument("attributes", new BsonDocument("passwordHash", DummyPasswordHash))) }
                    }
                }
            }
        };

        var json = MongoCommandSecretRedactor.ToRedactedJson(command);

        AssertNoDummy(json);
        Assert.Contains("\"attributes.userTokens.$[i0].attributes.value\" : \"***\"", json);
        // The array filter names a non-sensitive member and stays readable.
        Assert.Contains("\"i0.attributes.name\" : \"AuthenticatorKey\"", json);
    }

    [Fact]
    public void FilterOperators_AndElemMatch_BelowASensitiveAttribute_AreMasked()
    {
        var command = new BsonDocument
        {
            { "find", "System_Identity_User" },
            {
                "filter", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("attributes.securityStamp", new BsonDocument("$eq", DummySecurityStamp)),
                    new BsonDocument("attributes.userTokens", new BsonDocument("$elemMatch",
                        new BsonDocument("attributes.value", DummyAuthenticatorKey)))
                })
            }
        };

        var json = MongoCommandSecretRedactor.ToRedactedJson(command);

        AssertNoDummy(json);
        Assert.Contains("\"$eq\" : \"***\"", json);
        Assert.Contains("\"$elemMatch\"", json);
    }

    [Fact]
    public void ModelNames_AreMaskedInTheAttributeContextOnly()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pin", "Filter" };
        var command = new BsonDocument
        {
            { "find", "Demo_Device" },
            { "filter", new BsonDocument("attributes.pin", DummyPin) },
            { "projection", new BsonDocument("pin", 1) },
            {
                "let", new BsonDocument("attributes", new BsonDocument
                {
                    { "filter", "masked-as-a-model-attribute" },
                    { "visible", "stays" }
                })
            }
        };

        var json = MongoCommandSecretRedactor.ToRedactedJson(command, names);

        Assert.DoesNotContain(DummyPin, json);
        Assert.DoesNotContain("masked-as-a-model-attribute", json);
        // The command's own "filter" is not an attribute, nor is a top-level "pin".
        Assert.Contains("\"filter\" : { \"attributes.pin\" : \"***\" }", json);
        Assert.Contains("\"projection\" : { \"pin\" : 1 }", json);
        Assert.Contains("\"visible\" : \"stays\"", json);
    }

    [Fact]
    public void ExplicitNames_StillMask_ButTheDefaultIsTheKnownCredentialSet()
    {
        // Without names the known credential fields apply.
        Assert.DoesNotContain(DummyPasswordHash, MongoCommandSecretRedactor.ToRedactedJson(UserInsert(), null));

        // An empty set masks nothing by name (the envelope redaction stays, see the next test).
        var unmasked = MongoCommandSecretRedactor.ToRedactedJson(UserInsert(), new HashSet<string>());
        Assert.Contains(DummyPasswordHash, unmasked);
    }

    [Fact]
    public void SecretEnvelope_OutsideASensitiveAttribute_KeepsTheExistingRedaction()
    {
        var command = new BsonDocument
        {
            { "update", "Demo_Provider" },
            {
                "updates", new BsonArray
                {
                    new BsonDocument("u", new BsonDocument("$set", new BsonDocument
                    {
                        { "attributes.clientSecret", new BsonDocument { { "_t", "OctoSecret" }, { "e", FakeEnvelope } } },
                        { "attributes.passwordHash", new BsonDocument { { "_t", "OctoSecret" }, { "e", FakeEnvelope } } }
                    }))
                }
            }
        };

        var json = MongoCommandSecretRedactor.ToRedactedJson(command);

        Assert.DoesNotContain(FakeEnvelope, json);
        Assert.Contains("\"attributes.clientSecret\" : { \"_t\" : \"OctoSecret\", \"e\" : \"***\" }", json);
        Assert.Contains("\"attributes.passwordHash\" : { \"_t\" : \"***\", \"e\" : \"***\" }", json);
    }

    [Fact]
    public void Redact_IsCopyOnWrite_InputUntouched_AndPlainCommandsUnchanged()
    {
        var command = UserInsert();
        var before = command.ToJson();

        var redacted = MongoCommandSecretRedactor.Redact(command);

        Assert.NotSame(command, redacted);
        Assert.Equal(before, command.ToJson());

        var plain = new BsonDocument
        {
            { "find", "System_Identity_User" },
            { "filter", new BsonDocument("attributes.normalizedUserName", "DUMMY.USER") }
        };
        Assert.Same(plain, MongoCommandSecretRedactor.Redact(plain));
        Assert.Equal(plain.ToJson(), MongoCommandSecretRedactor.ToRedactedJson(plain));
    }

    [Fact]
    public void RawBsonDocument_UserInsert_IsMasked()
    {
        var raw = new RawBsonDocument(UserInsert().ToBson());

        AssertNoDummy(MongoCommandSecretRedactor.ToRedactedJson(raw));
    }

    [Fact]
    public void TruncateBson_MasksBeforeTruncation()
    {
        var full = MongoCommandObservability.TruncateBson(ClientSecretUpdate(), maxBytes: 4096);
        var truncated = MongoCommandObservability.TruncateBson(ClientSecretUpdate(), maxBytes: 150);

        AssertNoDummy(full);
        AssertNoDummy(truncated);
    }

    [Theory]
    [InlineData("passwordHash", true, true)]
    [InlineData("passwordHash", false, false)]
    [InlineData("PasswordHash", true, true)]
    [InlineData("attributes.passwordHash", false, true)]
    [InlineData("i0.attributes.passwordHash", false, true)]
    [InlineData("attributes.userTokens.$[i0].attributes.value", false, true)]
    [InlineData("passwordHash.attributes", false, false)]
    [InlineData("attributes.normalizedEmail", false, false)]
    [InlineData("filter", true, false)]
    public void IsSensitiveElementName_FollowsTheAttributeContext(string name, bool inAttributes, bool expected)
    {
        Assert.Equal(expected, MongoCommandSecretRedactor.IsSensitiveElementName(name, inAttributes,
            MongoCommandSensitiveAttributeNames.KnownCredentialNames));
    }

    internal static void AssertNoDummy(string? text)
    {
        Assert.NotNull(text);
        foreach (var dummy in AllDummies)
        {
            Assert.DoesNotContain(dummy, text);
        }

        // A truncation cut must not leave a recognisable prefix behind either.
        Assert.DoesNotContain("DUMMY-PASSWORD", text);
        Assert.DoesNotContain("DUMMY-CLIENT-SECRET", text);
    }
}
