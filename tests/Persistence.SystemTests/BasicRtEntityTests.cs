using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;
using Xunit.Abstractions;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class BasicRtEntityTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;
    private readonly ITestOutputHelper _testOutputHelper;

    public BasicRtEntityTests(SystemFixture systemFixture, ITestOutputHelper testOutputHelper)
    {
        _systemFixture = systemFixture;
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task CreateRtEntity()
    {
        var systemContext = _systemFixture.GetSystemContext();

        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(new CkModelId("Test"), operationResult);

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            for (var i = 0; i < 5; i++)
            {
                var rtPlanet = await tenantRepository.CreateTransientRtEntityAsync<RtPlanet>();
                rtPlanet.Designation = "test" + i;
                await tenantRepository.InsertOneRtEntityAsync(session, rtPlanet);
            }

            for (var i = 0; i < 5; i++)
            {
                var rtCity = await tenantRepository.CreateTransientRtEntityAsync<RtCity>();
                rtCity.Designation = "test" + i;
                await tenantRepository.InsertOneRtEntityAsync(session, rtCity);
            }

            await session.CommitTransactionAsync();

            session.StartTransaction();
            var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtCity>(session, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            Assert.Equal(10, y.Items.Count());
        }
        catch (Exception e)
        {
            _testOutputHelper.WriteLine(e.ToString());
            await session.AbortTransactionAsync();
            throw;
        }
    }
}