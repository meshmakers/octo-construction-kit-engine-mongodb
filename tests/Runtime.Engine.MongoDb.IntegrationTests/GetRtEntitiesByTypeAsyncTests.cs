using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class GetRtEntitiesByTypeAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture) : IClassFixture<SampleRtModelDataFixture>
{
    [Fact]
    public async Task GetRtEntitiesByTypeAsync_Filter_In_StringArray_OK()
    {
        var systemContext = sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var tags = new[] { "Water" };
        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter("Tags", FieldFilterOperator.In, tags);

        var result =
            await tenantRepository.GetRtEntitiesByTypeAsync(session, "Test/MeasuringPoint", queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
    }
    
   

    [Fact]
    public async Task GetRtEntitiesByTypeAsync_Filter_Equal_String_OK()
    {
        var designation = "Pinzgau / Zell am See";

        var systemContext = sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtStateOrProvince.Name), FieldFilterOperator.Equals, designation);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtDistrict>(session, queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
    }


    [Fact]
    public async Task GetRtEntitiesByTypeAsync_Filter_Like_OK()
    {
        var systemContext = sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtDistrict.Name), FieldFilterOperator.Like, "P*");

        var deep = await tenantRepository.GetRtEntitiesByTypeAsync<RtDistrict>(session, queryOptions,
             0, 5);

        Assert.Equal(2, deep.TotalCount);
    }
}
