using System.Dynamic;

using Meshmakers.Common.Shared;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5148 / AB#5160: an array attribute must persist as a BSON <b>array</b>, never as the MongoDB
///     driver's polymorphic <c>{ "_t": …, "_v": [ … ] }</c> wrapper.
///     <para>
///         <see cref="RtAttributeDictionarySerializer" /> handed the <b>caller's</b>
///         <see cref="BsonSerializationArgs" /> — whose <c>NominalType</c> is the attribute dictionary,
///         not the value — to the serializer it looked up for the value. Every driver collection
///         serializer treats "actual type ≠ nominal type" as a polymorphic write and emits the
///         discriminated wrapper instead of the bare value. The wrapper reads back as an
///         <see cref="ExpandoObject" /> (no <c>_t</c>, no <c>ckRecordId</c>), so every typed accessor
///         on that attribute — <c>GetAttributeStringValues</c>, <c>GetRtRecordAttributeValues</c>,
///         <c>EntityRuleEngine.SetDefaultValuesOnInsert</c> — then throws for the rest of the entity's
///         life.
///     </para>
/// </summary>
public class AttributeArrayValuePolymorphicWrapperTests
{
    private static readonly RtCkId<CkRecordId> MailRecordId = new("Test/EMailAddress");

    public static TheoryData<string, object> ArrayAttributeValues() => new()
    {
        { "Strings", new List<string> { "a", "b" } },
        // The shape EVERY array attribute has after a read: the dynamic array serializer
        // materializes a BSON array into List<object>, so a plain read-modify-write round trip
        // persists exactly this.
        { "Mixed", new List<object> { "a", "b" } },
        { "Longs", new List<long> { 1L, 2L } },
        { "Ints", new List<int> { 1, 2 } },
        { "Doubles", new List<double> { 1.5d, 2.5d } },
        { "Records", new List<RtRecord> { NewMailRecord() } },
        { "MixedRecords", new List<object> { NewMailRecord() } }
    };

    [Theory]
    [MemberData(nameof(ArrayAttributeValues))]
    public void Serialize_ArrayAttribute_WritesBsonArrayNotADiscriminatedWrapper(string attributeName, object value)
    {
        var attributes = SerializeAttributes(new Dictionary<string, object?> { [attributeName] = value });

        var element = attributes[attributeName.ToCamelCase()];
        Assert.True(element.IsBsonArray,
            $"'{attributeName}' must persist as a BSON array, but was {element.BsonType}: {element.ToJson()}");
    }

    /// <summary>
    ///     A scalar attribute must keep persisting as its bare BSON value — the fix must not turn
    ///     non-collection values into documents either.
    /// </summary>
    [Fact]
    public void Serialize_ScalarAttributes_KeepTheirBareBsonValue()
    {
        var attributes = SerializeAttributes(new Dictionary<string, object?>
        {
            ["Name"] = "octo",
            ["Count"] = 42,
            ["Ratio"] = 1.5d,
            ["Flag"] = true,
            ["Stamp"] = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(BsonType.String, attributes["name"].BsonType);
        Assert.Equal(BsonType.Int32, attributes["count"].BsonType);
        Assert.Equal(BsonType.Double, attributes["ratio"].BsonType);
        Assert.Equal(BsonType.Boolean, attributes["flag"].BsonType);
        Assert.Equal(BsonType.DateTime, attributes["stamp"].BsonType);
    }

    /// <summary>
    ///     Defense in depth for documents already poisoned by the wrapper write (mirrors the
    ///     attributes-null healing of AB#5148): the read must unwrap <c>{ "_v": [ … ] }</c> back into a
    ///     list instead of handing an <see cref="ExpandoObject" /> to the typed accessors. The next
    ///     write then persists the healed value as a proper array again.
    /// </summary>
    [Fact]
    public void Deserialize_PolymorphicWrapperWrittenByAnOlderBuild_IsUnwrapped()
    {
        var stored = new BsonDocument
        {
            {
                "attributes", new BsonDocument
                {
                    { "allowedGrantTypes", new BsonDocument { { "_v", new BsonArray { "code", "client_credentials" } } } }
                }
            }
        };

        var attributes = DeserializeAttributes(stored);

        var value = Assert.IsType<List<object>>(attributes["AllowedGrantTypes"]);
        Assert.Equal(["code", "client_credentials"], value.Cast<string>());
    }

    private static RtRecord NewMailRecord() =>
        new(MailRecordId, new Dictionary<string, object?> { ["EMailAddress"] = "a@b.c" });

    private static BsonDocument SerializeAttributes(Dictionary<string, object?> values)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        var context = BsonSerializationContext.CreateRoot(writer);

        writer.WriteStartDocument();
        writer.WriteName("attributes");
        // NominalType deliberately left at the caller's dictionary type — that is exactly what
        // DictionarySerializerBase passes down and what produced the wrapper.
        new RtAttributeDictionarySerializer().Serialize(context,
            new BsonSerializationArgs { NominalType = typeof(Dictionary<string, object?>) }, values);
        writer.WriteEndDocument();

        return document["attributes"].AsBsonDocument;
    }

    private static Dictionary<string, object?> DeserializeAttributes(BsonDocument stored)
    {
        using var reader = new BsonDocumentReader(stored);
        var context = BsonDeserializationContext.CreateRoot(reader);

        reader.ReadStartDocument();
        reader.ReadName();
        var result = new RtAttributeDictionarySerializer().Deserialize(context, new BsonDeserializationArgs());
        reader.ReadEndDocument();

        return result;
    }
}
