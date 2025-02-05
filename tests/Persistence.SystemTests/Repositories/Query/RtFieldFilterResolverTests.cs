using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Repositories.Query;

[Collection("Sequential")]
public class RtFieldFilterResolverTests(ImportTestCkModelFixture systemFixture)
    : IClassFixture<ImportTestCkModelFixture>
{
    [Theory]
    [InlineData("Address")]
    [InlineData("Address.City")]
    [InlineData("EMailAddresses[0]")]
    [InlineData("EMailAddresses[-1]")]
    [InlineData("EMailAddresses[*]")]
    [InlineData("EMailAddresses[0].EMailAddress")]
    [InlineData("EMailAddresses[-1].EMailAddress")]
    [InlineData("EMailAddresses[*].EMailAddress")]
    public async Task IsAttributePathValid_OK(string attributePath)
    {
        var resolver = await Prepare();

        Assert.True(resolver.IsAttributePathValid(attributePath));
    }

    [Theory]
    [InlineData("address")]
    [InlineData("address.city")]
    [InlineData("eMailAddresses[0]")]
    [InlineData("eMailAddresses[-1]")]
    [InlineData("eMailAddresses[*]")]
    [InlineData("eMailAddresses[0].eMailAddress")]
    [InlineData("eMailAddresses[-1].eMailAddress")]
    [InlineData("eMailAddresses[*].eMailAddress")]
    public async Task IsAttributePathValid_CamelCase_OK(string attributePath)
    {
        var resolver = await Prepare();

        Assert.True(resolver.IsAttributePathValid(attributePath));
    }

    [Theory]
    [InlineData("Address", "attributes.address")]
    [InlineData("Address.City", "attributes.address.attributes.city")]
    [InlineData("EMailAddresses[0]", "attributes.eMailAddresses[0]")]
    [InlineData("EMailAddresses[-1]", "attributes.eMailAddresses[-1]")]
    [InlineData("EMailAddresses[*]", "attributes.eMailAddresses[*]")]
    [InlineData("EMailAddresses[0].EMailAddress", "attributes.eMailAddresses[0].attributes.eMailAddress")]
    [InlineData("EMailAddresses[-1].EMailAddress", "attributes.eMailAddresses[-1].attributes.eMailAddress")]
    [InlineData("EMailAddresses[*].EMailAddress", "attributes.eMailAddresses[*].attributes.eMailAddress")]
    public async Task ResolveAttributePath_Attributes_OK(string attributePath, string expectedPath)
    {
        var resolver = await Prepare();

        Assert.Equal(expectedPath, resolver.ResolveAttributePath(attributePath));
    }

    private async Task<RtFieldFilterResolver<RtCustomer>> Prepare()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        await systemContext.LoadCacheForTenantAsync();
        var ckCacheService = systemFixture.GetService<ICkCacheService>();
        var ckTypeGraph = ckCacheService.GetCkType(systemContext.TenantId,
            new CkId<CkTypeId>(TestCkIds.ModelId, TestCkIds.CustomerTypeId));

        var resolver = A.Fake<RtFieldFilterResolver<RtCustomer>>(o => o.
            WithArgumentsForConstructor([ckCacheService, systemContext.TenantId, ckTypeGraph]).CallsBaseMethods()
        );
        return resolver;
    }
}
