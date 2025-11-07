using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection("Sequential")]
public class FieldFilterResolverTests
    : IClassFixture<ImportTestCkModelFixture> // We need the class fixture to register bson class maps correctly.
{
    [Fact]
    public void GetEntityName_OK()
    {
        FieldFilterResolver<RtTagsItem> resolver = new();

        Assert.Equal("RtTagsItem", resolver.GetEntityName());
    }

    [Fact]
    public void IsAttributePathValid_Root_AttributeName_OK()
    {
        FieldFilterResolver<RtCustomer> resolver = new();

        Assert.True(resolver.IsAttributePathValid("Address"));
    }
}