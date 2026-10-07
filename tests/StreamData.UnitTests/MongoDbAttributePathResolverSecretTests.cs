using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     AB#5533: terminal value-type resolution behind the Secret query refusals and the index guard,
///     plus the CrateDB primitive mapping that must never produce a column for a Secret attribute.
/// </summary>
public class MongoDbAttributePathResolverSecretTests
{
    private static FakeProvider Model()
    {
        var credential = new FakeProvider(isRecord: true)
            .WithAttribute("Key", AttributeValueTypesDto.String)
            .WithAttribute("Value", AttributeValueTypesDto.Secret);

        return new FakeProvider()
            .WithAttribute("Name", AttributeValueTypesDto.String)
            .WithAttribute("ApiKey", AttributeValueTypesDto.Secret)
            .WithAttribute("Credentials", AttributeValueTypesDto.RecordArray)
            .WithRecordChild("Credentials", credential)
            .WithAttribute("PrimaryCredential", AttributeValueTypesDto.Record)
            .WithRecordChild("PrimaryCredential", credential);
    }

    [Theory]
    [InlineData("ApiKey", true)]
    [InlineData("apiKey", true)]
    [InlineData("Credentials.Value", true)]
    [InlineData("Credentials[0].Value", true)]
    [InlineData("credentials[*].value", true)]
    [InlineData("PrimaryCredential.Value", true)]
    [InlineData("Name", false)]
    [InlineData("Credentials", false)]
    [InlineData("Credentials.Key", false)]
    [InlineData("PrimaryCredential.Key", false)]
    [InlineData("Unknown", false)]
    [InlineData("Name.Value", false)]
    public void IsSecretAttributePath_ResolvesTheTerminalAttribute(string path, bool expected)
    {
        Assert.Equal(expected, MongoDbAttributePathResolver.IsSecretAttributePath(path, Model()));
    }

    [Fact]
    public void TryResolveTerminalValueType_ReturnsTheRecordTypeForARecordPath()
    {
        Assert.True(MongoDbAttributePathResolver.TryResolveTerminalValueType("Credentials", Model(), out var type));
        Assert.Equal(AttributeValueTypesDto.RecordArray, type);
    }

    [Fact]
    public void WithoutSecretAttributePaths_DropsSecretFieldsFromAnIndexDefinition()
    {
        var skipped = new List<string>();

        var kept = MongoDbAttributePathResolver.WithoutSecretAttributePaths(
            ["Name", "ApiKey", "Credentials.Key", "Credentials.Value", "rtState"], Model(), skipped.Add).ToList();

        Assert.Equal(["Name", "Credentials.Key", "rtState"], kept);
        Assert.Equal(["ApiKey", "Credentials.Value"], skipped);
    }

    [Fact]
    public void CrateTypeMapper_RefusesSecret()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CrateTypeMapper.ToCratePrimitive(AttributeValueTypesDto.Secret));
        Assert.Contains("Secret", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeProvider(bool isRecord = false) : IAttributeMetadataProvider
    {
        private readonly Dictionary<string, AttributeValueTypesDto> _attributes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IAttributeMetadataProvider> _children = new(StringComparer.Ordinal);

        public bool IsRecordContext { get; } = isRecord;

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
