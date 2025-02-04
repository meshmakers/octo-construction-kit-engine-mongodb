using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class GetRtAssociationTargetsAsyncTests(GenerateSampleDataFixture generateSampleDataFixture)
    : IClassFixture<GenerateSampleDataFixture>
{
    [Fact]
    public async Task GetRtAssociationTargetsAsync_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var dataQueryOperation = DataQueryOperation.Create();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, new CkId<CkTypeId>(TestCkIds.ModelId, TestCkIds.DistrictTypeId),
            dataQueryOperation, 0, 5);

        var rtIds = result.Items.Select(x => x.RtId).ToList();
        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, rtIds,
            new CkId<CkTypeId>(TestCkIds.ModelId, TestCkIds.DistrictTypeId),
            new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild), 
            new CkId<CkTypeId>(TestCkIds.ModelId, TestCkIds.MunicipalityTypeId),
            GraphDirections.Inbound, null,
            dataQueryOperation, 0, 5);

        Assert.Equal(5, deep.Count);
    }
}