using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Behavior pinned for MongoDbAttributePathResolver.TryReverseToCkPath — the Stage 2D inverse
// of ResolveToMongoDbFieldPath. Lets the slow-query index suggester translate a captured
// MongoDB filter path back to a PascalCase CK attribute path for CK-YAML emission.
public class MongoDbAttributePathResolverReverseTests
{
    // ---- null / empty / wrong-prefix short-circuits --------------------------------------

    [Fact]
    public void TryReverseToCkPath_NullInput_ReturnsNull()
    {
        Assert.Null(MongoDbAttributePathResolver.TryReverseToCkPath(null!, new FakeProvider()));
    }

    [Fact]
    public void TryReverseToCkPath_EmptyInput_ReturnsNull()
    {
        Assert.Null(MongoDbAttributePathResolver.TryReverseToCkPath(string.Empty, new FakeProvider()));
    }

    [Theory]
    [InlineData("ckTypeId.fullName")]
    [InlineData("_id")]
    [InlineData("rtId")]
    [InlineData("createdAt")]
    [InlineData("attributesx")] // looks like the prefix but isn't
    public void TryReverseToCkPath_PathWithoutAttributesPrefix_ReturnsNull(string mongoPath)
    {
        // Stage 2D consciously skips non-attribute fields — they don't have a CK-YAML home.
        Assert.Null(MongoDbAttributePathResolver.TryReverseToCkPath(mongoPath, new FakeProvider()));
    }

    [Fact]
    public void TryReverseToCkPath_EmptyAfterAttributesPrefix_ReturnsNull()
    {
        Assert.Null(MongoDbAttributePathResolver.TryReverseToCkPath("attributes.", new FakeProvider()));
    }

    // ---- single scalar attribute --------------------------------------------------------

    [Fact]
    public void TryReverseToCkPath_ScalarWithValueSuffix_ReturnsPascalCaseName()
    {
        // The most common shape captured from a slow-query filter:
        // {"attributes.name.value": "Salzburg"}. Strip the .value, look up the camelCase name
        // as a PascalCase CK attribute.
        var provider = new FakeProvider().WithAttribute("Name", AttributeValueTypesDto.String);

        var result = MongoDbAttributePathResolver.TryReverseToCkPath("attributes.name.value", provider);

        Assert.Equal("Name", result);
    }

    [Fact]
    public void TryReverseToCkPath_ScalarWithoutValueSuffix_AlsoSupported()
    {
        // The forward function does NOT append .value (the BSON filter builder does
        // downstream). Reverse must accept both shapes — captured filter path with .value,
        // and freshly-built path without.
        var provider = new FakeProvider().WithAttribute("Name", AttributeValueTypesDto.String);

        var result = MongoDbAttributePathResolver.TryReverseToCkPath("attributes.name", provider);

        Assert.Equal("Name", result);
    }

    [Fact]
    public void TryReverseToCkPath_AttributeNotInProvider_ReturnsNull()
    {
        // The CK type doesn't declare this attribute — could be a field on a different type
        // (the slow-query buffer doesn't always carry a type discriminator), or a recently
        // removed attribute. Reverse returns null so the caller skips CK-YAML emission.
        var provider = new FakeProvider(); // no attributes

        Assert.Null(MongoDbAttributePathResolver.TryReverseToCkPath("attributes.unknown.value", provider));
    }

    // ---- nested record paths ------------------------------------------------------------

    [Fact]
    public void TryReverseToCkPath_NestedRecord_ReturnsDottedPascalCase()
    {
        // {filter: {"attributes.timeRange.attributes.from.value": ...}}
        // forward function emits  attributes.timeRange.attributes.from
        // reverse must walk back: TimeRange (record) → From (scalar)
        var inner = new FakeProvider(isRecord: true).WithAttribute("From", AttributeValueTypesDto.DateTime);
        var outer = new FakeProvider()
            .WithAttribute("TimeRange", AttributeValueTypesDto.Record)
            .WithRecordChild("TimeRange", inner);

        var result = MongoDbAttributePathResolver.TryReverseToCkPath(
            "attributes.timeRange.attributes.from.value", outer);

        Assert.Equal("TimeRange.From", result);
    }

    [Fact]
    public void TryReverseToCkPath_DeepNestedRecord_WalksWholeChain()
    {
        // attributes.location.attributes.address.attributes.city.value → Location.Address.City
        var cityProvider = new FakeProvider(isRecord: true).WithAttribute("City", AttributeValueTypesDto.String);
        var addressProvider = new FakeProvider(isRecord: true)
            .WithAttribute("Address", AttributeValueTypesDto.Record)
            .WithRecordChild("Address", cityProvider);
        var rootProvider = new FakeProvider()
            .WithAttribute("Location", AttributeValueTypesDto.Record)
            .WithRecordChild("Location", addressProvider);

        var result = MongoDbAttributePathResolver.TryReverseToCkPath(
            "attributes.location.attributes.address.attributes.city.value", rootProvider);

        Assert.Equal("Location.Address.City", result);
    }

    [Fact]
    public void TryReverseToCkPath_RecordChildMissingFromCache_ReturnsNull()
    {
        // The provider knows the record-typed parent exists, but NavigateToRecord returns
        // null — record graph isn't loaded. Reverse must bail rather than emit half a path.
        var outer = new FakeProvider().WithAttribute("TimeRange", AttributeValueTypesDto.Record);
        // intentionally NOT calling WithRecordChild — NavigateToRecord returns null

        var result = MongoDbAttributePathResolver.TryReverseToCkPath(
            "attributes.timeRange.attributes.from.value", outer);

        Assert.Null(result);
    }

    // ---- malformed paths ----------------------------------------------------------------

    [Fact]
    public void TryReverseToCkPath_MissingAttributesSeparator_ReturnsNull()
    {
        // attributes.timeRange.from.value — without the .attributes. separator between
        // record and child, the path doesn't fit the forward function's output shape.
        var inner = new FakeProvider(isRecord: true).WithAttribute("From", AttributeValueTypesDto.DateTime);
        var outer = new FakeProvider()
            .WithAttribute("TimeRange", AttributeValueTypesDto.Record)
            .WithRecordChild("TimeRange", inner);

        var result = MongoDbAttributePathResolver.TryReverseToCkPath(
            "attributes.timeRange.from.value", outer);

        Assert.Null(result);
    }

    [Fact]
    public void TryReverseToCkPath_ScalarWithExtraSegments_ReturnsNull()
    {
        // attributes.name.attributes.extra — Name is scalar, can't have nested attrs.
        var provider = new FakeProvider().WithAttribute("Name", AttributeValueTypesDto.String);

        var result = MongoDbAttributePathResolver.TryReverseToCkPath(
            "attributes.name.attributes.extra", provider);

        Assert.Null(result);
    }

    // ---- minimal stub for IAttributeMetadataProvider ----

    /// <summary>
    /// In-test stand-in for the production <see cref="CkCacheAttributeMetadataProvider"/>.
    /// Lets each test wire up the attribute set + nested record graph it needs without
    /// dragging the full CK cache + tenant infrastructure into a unit test.
    /// </summary>
    private sealed class FakeProvider : IAttributeMetadataProvider
    {
        private readonly Dictionary<string, AttributeValueTypesDto> _attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IAttributeMetadataProvider> _children = new(StringComparer.Ordinal);

        public FakeProvider(bool isRecord = false)
        {
            IsRecordContext = isRecord;
        }

        public bool IsRecordContext { get; }

        public bool TryGetAttribute(string attributeName, out AttributeValueTypesDto valueType)
            => _attributes.TryGetValue(attributeName, out valueType);

        public IAttributeMetadataProvider? NavigateToRecord(string attributeName)
            => _children.TryGetValue(attributeName, out var child) ? child : null;

        public FakeProvider WithAttribute(string name, AttributeValueTypesDto type)
        {
            _attributes[name] = type;
            return this;
        }

        public FakeProvider WithRecordChild(string name, IAttributeMetadataProvider child)
        {
            _children[name] = child;
            return this;
        }
    }
}
