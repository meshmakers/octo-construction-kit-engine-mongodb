using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class GetIndirectRtAssociationTargetsAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture) : IClassFixture<SampleRtModelDataFixture>
{
    [Fact]
    public async Task GetIndirectRtAssociationTargetsAsync_OK()
    {
        var systemContext = sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var originRtId = OctoObjectId.Parse(KnownRtIds.SalzburgStateOrProvince);

        var r = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtStateOrProvince, RtMunicipality>(session, originRtId, SystemCkIds.RtCkParentChildRoleId,
            GraphDirections.Inbound);
        
        await session.CommitTransactionAsync();
        
        Assert.NotNull(r);
        Assert.Equal(2, r.TotalCount);
    }
}
