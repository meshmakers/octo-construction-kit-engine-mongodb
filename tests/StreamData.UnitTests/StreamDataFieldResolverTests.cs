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

    [Theory]
    [InlineData("window_start")]
    [InlineData("window_end")]
    [InlineData("was_updated")]
    public void WindowedResolver_AcceptsWindowedDefaultFields(string fieldName)
    {
        // Windowed archives expose (window_start, window_end, was_updated) instead of timestamp.
        var resolver = new StreamDataFieldResolver([], usesWindowedStorage: true);

        var result = resolver.Resolve(fieldName);

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal(fieldName, result.CrateDbName);
    }

    [Fact]
    public void WindowedResolver_DoesNotRegisterTimestampDefault()
    {
        // The single-timestamp column doesn't exist on windowed tables.
        var resolver = new StreamDataFieldResolver([], usesWindowedStorage: true);

        Assert.Null(resolver.Resolve("timestamp"));
    }

    [Fact]
    public void RawResolver_DoesNotRegisterWindowedDefaults()
    {
        // The window_* / was_updated columns don't exist on raw archive tables.
        var resolver = new StreamDataFieldResolver([], usesWindowedStorage: false);

        Assert.Null(resolver.Resolve("window_start"));
        Assert.Null(resolver.Resolve("window_end"));
        Assert.Null(resolver.Resolve("was_updated"));
    }

    [Fact]
    public void WindowedResolver_KeepsSharedRtDefaults()
    {
        // Rt* columns are present on both shapes — only the time-axis columns differ.
        var resolver = new StreamDataFieldResolver([], usesWindowedStorage: true);

        Assert.NotNull(resolver.Resolve("rtId"));
        Assert.NotNull(resolver.Resolve("ckTypeId"));
        Assert.NotNull(resolver.Resolve("rtWellKnownName"));
        Assert.NotNull(resolver.Resolve("rtCreationDateTime"));
        Assert.NotNull(resolver.Resolve("rtChangedDateTime"));
    }
}
