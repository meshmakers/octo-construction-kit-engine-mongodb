using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class GetIndirectRtAssociationTargetsAsyncTests(GenerateSampleDataFixture generateSampleDataFixture) : IClassFixture<GenerateSampleDataFixture>
{
    [Fact]
    public async Task GetIndirectRtAssociationTargetsAsync_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var originRtId = OctoObjectId.Parse(KnownRtIds.SalzburgStateOrProvince);

        var r = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtStateOrProvince, RtMunicipality>(session, originRtId,
            new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
            GraphDirections.Inbound);
        
        await session.CommitTransactionAsync();
        
        Assert.NotNull(r);
        Assert.Equal(2, r.TotalCount);
    }
}