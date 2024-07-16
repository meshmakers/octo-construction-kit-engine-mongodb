using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class InsertOneRtEntityAsyncTests(ImportTestCkModelFixture systemFixture)
    : IClassFixture<ImportTestCkModelFixture>
{
    [Fact]
    public async void CreateRtEntity()
    {
        var systemContext = systemFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        for (var i = 0; i < 5; i++)
        {
            var rtPlanet = await tenantRepository.CreateTransientRtEntityAsync<RtStateOrProvince>();
            rtPlanet.Name = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(session, rtPlanet);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtStateOrProvince>(session2,
            DataQueryOperation.Create());

        await session2.CommitTransactionAsync();

        Assert.Equal(5, y.Items.Count());
    }
}