using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;


using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>AB#7018: the text of an E11000 is the only place the driver puts the index name and the key.</summary>
public class DuplicateKeyMessageParserTests
{
    [Fact]
    public void Parse_PrimaryKeyViolation_ReturnsIndexNameAndKey()
    {
        var parsed = DuplicateKeyMessageParser.Parse(
            "E11000 duplicate key error collection: db.RtEntities index: _id_ dup key: { _id: ObjectId('66803ecf4aa85720dda96a97') }");

        Assert.Equal("_id_", parsed.IndexName);
        Assert.Equal("{ _id: ObjectId('66803ecf4aa85720dda96a97') }", parsed.DupKey);
    }

    [Fact]
    public void Parse_BusinessKeyViolation_ReturnsIndexNameAndKey()
    {
        var parsed = DuplicateKeyMessageParser.Parse(
            "E11000 duplicate key error collection: db.RtEntities index: ItemCode_1 dup key: { attributes.ItemCode: \"ITEM-0001\" }");

        Assert.Equal("ItemCode_1", parsed.IndexName);
        Assert.Equal("{ attributes.ItemCode: \"ITEM-0001\" }", parsed.DupKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else failed")]
    public void Parse_NoDuplicateKeyText_ReturnsNothing(string? message)
    {
        var parsed = DuplicateKeyMessageParser.Parse(message);

        Assert.Null(parsed.IndexName);
        Assert.Null(parsed.DupKey);
    }

    [Fact]
    public void BuildKeyFilter_QuotesDottedFieldNames()
    {
        var filter = DuplicateKeyMessageParser.BuildKeyFilter<BsonDocument>("{ attributes.ItemCode: \"ITEM-0001\" }");

        Assert.NotNull(filter);
        var rendered = filter.Render(new RenderArgs<BsonDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));
        Assert.Equal("ITEM-0001", rendered["attributes.ItemCode"].AsString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json at all")]
    public void BuildKeyFilter_UnreadableKey_ReturnsNull(string? dupKey)
    {
        Assert.Null(DuplicateKeyMessageParser.BuildKeyFilter<RtEntity>(dupKey));
    }
}
