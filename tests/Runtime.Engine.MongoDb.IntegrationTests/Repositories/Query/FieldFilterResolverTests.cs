using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

// We need the shared collection fixture to register BSON class maps correctly.
[Collection(ImportTestCkModelCollection.Name)]
public class FieldFilterResolverTests
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