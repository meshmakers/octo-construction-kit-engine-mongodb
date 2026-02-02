using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection("Sequential")]
public class RtEntityFieldFilterResolverTests(ImportTestCkModelFixture systemFixture)
    : IClassFixture<ImportTestCkModelFixture>
{
    [Theory]
    [InlineData("RtId")]
    [InlineData("CkTypeId")]
    [InlineData("RtWellKnownName")]
    [InlineData("Address")]
    [InlineData("Address.City")]
    [InlineData("EMailAddresses[0]")]
    [InlineData("EMailAddresses[*]")]
    [InlineData("EMailAddresses[0].EMailAddress")]
    [InlineData("EMailAddresses[*].EMailAddress")]
    public async Task IsAttributePathValid_OK(string attributePath)
    {
        var resolver = await Prepare();

        Assert.True(resolver.IsAttributePathValid(attributePath));
    }

    [Theory]
    [InlineData("Address", "attributes.address")]
    [InlineData("Address.City", "attributes.address.attributes.city")]
    [InlineData("EMailAddresses[0]", "attributes.eMailAddresses.0")]
    [InlineData("EMailAddresses[*]", "attributes.eMailAddresses")]
    [InlineData("EMailAddresses[0].EMailAddress", "attributes.eMailAddresses.0.attributes.eMailAddress")]
    [InlineData("EMailAddresses[*].EMailAddress", "attributes.eMailAddresses.attributes.eMailAddress")]
    public async Task ResolveAttributePath_Attributes_OK(string attributePath, string expectedPath)
    {
        var resolver = await Prepare();

        Assert.Equal(expectedPath, resolver.ResolveAttributePath(attributePath));
    }

    [Theory]
    [InlineData("NotExistingAttribute")]
    [InlineData("Address.NotExistingAttribute")]
    [InlineData("EMailAddresses[0].NotExistingAttribute")]
    public async Task ResolveAttributePath_Attributes_DoNotExist_Fail(string attributePath)
    {
        var resolver = await Prepare();

        Assert.Null(resolver.ResolveAttributePath(attributePath));
    }

    [Theory]
    [InlineData("RtId", "_id")]
    [InlineData("CkTypeId", "ckTypeId")]
    [InlineData("RtWellKnownName", "rtWellKnownName")]
    [InlineData("RtCreationDateTime", "rtCreationDateTime")]
    [InlineData("RtChangedDateTime", "rtChangedDateTime")]
    public async Task ResolveAttributePath_SystemAttributes_OK(string attributePath, string expectedPath)
    {
        var resolver = await Prepare();

        Assert.Equal(expectedPath, resolver.ResolveAttributePath(attributePath));
    }

    [Fact]
    public async Task ResolveSearchAttributeValue_CkTypeId_Equals_ReturnsRtCkId()
    {
        // Arrange
        var resolver = await Prepare();
        const string ckTypeIdValue = "Test/Customer";

        // Act
        var result = resolver.ResolveSearchAttributeValue("CkTypeId", ckTypeIdValue, FieldFilterOperator.Equals, out var isEnum);

        // Assert
        Assert.False(isEnum);
        Assert.IsType<RtCkId<CkTypeId>>(result);
        var rtCkId = (RtCkId<CkTypeId>)result!;
        Assert.Equal("Test", rtCkId.ModelId);
        Assert.Equal("Customer", rtCkId.ElementId.Name);
    }

    [Fact]
    public async Task ResolveSearchAttributeValue_CkTypeId_Like_ReturnsString()
    {
        // Arrange
        var resolver = await Prepare();
        const string searchTerm = "Customer";

        // Act
        var result = resolver.ResolveSearchAttributeValue("CkTypeId", searchTerm, FieldFilterOperator.Like, out var isEnum);

        // Assert
        Assert.False(isEnum);
        Assert.IsType<string>(result);
        Assert.Equal(searchTerm, result);
    }

    [Theory]
    [InlineData(FieldFilterOperator.Like)]
    [InlineData(FieldFilterOperator.Contains)]
    [InlineData(FieldFilterOperator.StartsWith)]
    [InlineData(FieldFilterOperator.EndsWith)]
    [InlineData(FieldFilterOperator.MatchRegEx)]
    public async Task ResolveSearchAttributeValue_CkTypeId_StringBasedOperators_ReturnsString(FieldFilterOperator filterOperator)
    {
        // Arrange
        var resolver = await Prepare();
        const string searchTerm = "Customer";

        // Act
        var result = resolver.ResolveSearchAttributeValue("CkTypeId", searchTerm, filterOperator, out var isEnum);

        // Assert
        Assert.False(isEnum);
        Assert.IsType<string>(result);
        Assert.Equal(searchTerm, result);
    }

    private async Task<RtEntityFieldFilterResolver<RtCustomer>> Prepare()
   {
       await systemFixture.ClearCollectionAsync();
       var systemContext = systemFixture.GetSystemContext();
       await systemContext.LoadCacheForTenantAsync();
       var ckCacheService = systemFixture.GetService<ICkCacheService>();
       var ckTypeGraph = ckCacheService.GetCkType(systemContext.TenantId, TestCkIds.CkCustomerTypeId);

       var resolver = A.Fake<RtEntityFieldFilterResolver<RtCustomer>>(o => o.
           WithArgumentsForConstructor([ckCacheService, systemContext.TenantId, ckTypeGraph]).CallsBaseMethods()
       );
       return resolver;
   }
}
