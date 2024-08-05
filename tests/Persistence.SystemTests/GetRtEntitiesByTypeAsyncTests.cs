using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class GetRtEntitiesByTypeAsyncTests(GenerateSampleDataFixture generateSampleDataFixture) : IClassFixture<GenerateSampleDataFixture>
{
    [Fact]
    public async Task GetRtEntitiesByTypeAsync_Filter_In_StringArray_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var tags = new[] { "Water" };
        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter("Tags", FieldFilterOperator.In, tags);

        var result =
            await tenantRepository.GetRtEntitiesByTypeAsync(session, "Test/MeasuringPoint", dataQueryOperation);

        await session.CommitTransactionAsync();

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
    }
    
   

    [Fact]
    public async Task GetRtEntitiesByTypeAsync_Filter_Equal_String_OK()
    {
        var designation = "Pinzgau / Zell am See";

        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtStateOrProvince.Name), FieldFilterOperator.Equals, designation);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtDistrict>(session, dataQueryOperation);

        await session.CommitTransactionAsync();

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
    }


    [Fact]
    public async Task GetRtEntitiesByTypeAsync_Filter_Like_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtDistrict.Name), FieldFilterOperator.Like, "P*");

        var deep = await tenantRepository.GetRtEntitiesByTypeAsync<RtDistrict>(session, dataOperation,
             0, 5);

        Assert.Equal(2, deep.TotalCount);
    }
}