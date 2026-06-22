using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(SampleRtModelDataCollection.Name)]
public class GetIndirectRtAssociationTargetsAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture)
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
