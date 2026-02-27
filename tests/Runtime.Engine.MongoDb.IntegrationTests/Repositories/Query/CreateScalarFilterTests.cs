using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
/// Tests for <see cref="FieldFilterResolver{TEntity}.CreateScalarFilter"/>,
/// specifically verifying ComparisonValueToArray behavior for the In/NotIn operators.
/// Regression test for bug: single string without comma passed to In operator
/// resulted in an empty $in array, matching no documents.
/// </summary>
public class CreateScalarFilterTests
{
    [Fact]
    public void In_SingleStringValue_CreatesFilterWithSingleElementArray()
    {
        // Arrange - a single string value without commas (e.g. resolved from a JSONPath with 1 result)
        var value = "AT0040000542100000000000005037982";

        // Act
        var filter = FieldFilterResolver<BsonDocument>.CreateScalarFilter(
            "meteringPointNumber", FieldFilterOperator.In, value);

        // Assert
        var rendered = Render(filter);
        var inArray = rendered["meteringPointNumber"]["$in"].AsBsonArray;
        Assert.Single(inArray);
        Assert.Equal("AT0040000542100000000000005037982", inArray[0].AsString);
    }

    [Fact]
    public void In_CommaSeparatedStringValue_CreatesFilterWithSplitArray()
    {
        // Arrange - comma-separated string (legacy behavior)
        var value = "Value1,Value2,Value3";

        // Act
        var filter = FieldFilterResolver<BsonDocument>.CreateScalarFilter(
            "status", FieldFilterOperator.In, value);

        // Assert
        var rendered = Render(filter);
        var inArray = rendered["status"]["$in"].AsBsonArray;
        Assert.Equal(3, inArray.Count);
        Assert.Equal("Value1", inArray[0].AsString);
        Assert.Equal("Value2", inArray[1].AsString);
        Assert.Equal("Value3", inArray[2].AsString);
    }

    [Fact]
    public void In_ListOfStrings_CreatesFilterWithArray()
    {
        // Arrange - List<object> as typically resolved from JSONPath with multiple results
        var value = new List<object> { "L1", "L2" };

        // Act
        var filter = FieldFilterResolver<BsonDocument>.CreateScalarFilter(
            "dataQuality", FieldFilterOperator.In, value);

        // Assert
        var rendered = Render(filter);
        var inArray = rendered["dataQuality"]["$in"].AsBsonArray;
        Assert.Equal(2, inArray.Count);
        Assert.Equal("L1", inArray[0].AsString);
        Assert.Equal("L2", inArray[1].AsString);
    }

    [Fact]
    public void NotIn_SingleStringValue_CreatesFilterWithSingleElementArray()
    {
        // Arrange
        var value = "Excluded";

        // Act
        var filter = FieldFilterResolver<BsonDocument>.CreateScalarFilter(
            "status", FieldFilterOperator.NotIn, value);

        // Assert
        var rendered = Render(filter);
        var ninArray = rendered["status"]["$nin"].AsBsonArray;
        Assert.Single(ninArray);
        Assert.Equal("Excluded", ninArray[0].AsString);
    }

    private static BsonDocument Render(FilterDefinition<BsonDocument> filter)
    {
        var serializerRegistry = BsonSerializer.SerializerRegistry;
        var documentSerializer = serializerRegistry.GetSerializer<BsonDocument>();
        return filter.Render(new RenderArgs<BsonDocument>(documentSerializer, serializerRegistry));
    }
}
