using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// After T17 every field — default and data-stream alike — is a typed CrateDB column on the
// per-archive table. The resolver maps any-case input to the canonical camelCase column name
// and exposes the same string as the GraphQL alias. There is no longer an `IsDataField` flag.
public class StreamDataFieldResolverTests
{
    [Fact]
    public void DefaultField_PascalCase_ResolvesToCamelCase()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("RtId");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("rtid", result.CrateDbName);
        Assert.Equal("rtid", result.GraphQlAlias);
    }

    [Fact]
    public void DefaultField_CamelCase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("rtid");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("rtid", result.CrateDbName);
        Assert.Equal("rtid", result.GraphQlAlias);
    }

    [Fact]
    public void DefaultField_Uppercase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("RTID");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("rtid", result.CrateDbName);
    }

    [Fact]
    public void AllSixDefaultFields_ResolveCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var expectedDefaults = new[] { "timestamp", "rtid", "cktypeid", "rtwellknownname", "rtcreationdatetime", "rtchangeddatetime" };

        foreach (var field in expectedDefaults)
        {
            var result = resolver.Resolve(field);
            Assert.NotNull(result);
            Assert.Equal(StreamDataFieldCategory.Default, result.Category);
            Assert.Equal(field, result.CrateDbName);
        }
    }

    [Fact]
    public void DataStreamField_PicksCamelCasedColumnName()
    {
        var resolver = new StreamDataFieldResolver(["acknowledged", "voltage"]);

        var result = resolver.Resolve("acknowledged");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.DataStream, result.Category);
        Assert.Equal("acknowledged", result.CrateDbName);
        Assert.Equal("acknowledged", result.GraphQlAlias);
    }

    [Fact]
    public void DataStreamField_DottedPathMapsToLowerCasedColumn()
    {
        var resolver = new StreamDataFieldResolver(["sensor.reading.value"]);

        var result = resolver.Resolve("sensor.reading.value");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.DataStream, result.Category);
        Assert.Equal("sensorreadingvalue", result.CrateDbName);
    }

    [Fact]
    public void UnknownField_Resolve_ReturnsNull()
    {
        var resolver = new StreamDataFieldResolver(["voltage"]);

        var result = resolver.Resolve("nonExistent");

        Assert.Null(result);
    }

    [Fact]
    public void UnknownField_ResolveOrFallback_ReturnsLowerCased()
    {
        var resolver = new StreamDataFieldResolver(["voltage"]);

        var result = resolver.ResolveOrFallback("unknownField");

        Assert.Equal(StreamDataFieldCategory.Unknown, result.Category);
        Assert.Equal("unknownfield", result.CrateDbName);
    }

    [Fact]
    public void EmptyConstructor_StillResolvesDefaults()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("timestamp");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
    }

    [Fact]
    public void DefaultFieldNameAsDataAttribute_DefaultTakesPrecedence()
    {
        // If a data attribute has the same name as a default, the default wins.
        var resolver = new StreamDataFieldResolver(["timestamp"]);

        var result = resolver.Resolve("timestamp");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
    }
}
