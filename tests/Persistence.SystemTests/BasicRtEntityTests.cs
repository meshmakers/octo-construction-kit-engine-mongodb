using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class BasicRtEntityTests: IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;

    public BasicRtEntityTests(SystemFixture systemFixture)
    {
        _systemFixture = systemFixture;
    }

    [Fact]
    public async void CreateRtEntity()
    {
        var systemContext = _systemFixture.GetSystemContext();
        
        using var systemSession = await systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();

        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(systemSession, new CkModelId("System.Identity-1.0.0"), operationResult);
        await systemSession.CommitTransactionAsync();

        var tenantRepository = await systemContext.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var x = tenantRepository.CreateTransientRtEntity<RtUser>();
        x.UserName = "test";
        x.Email = "demo@demo.com";
        x.ConcurrencyStamp = Guid.NewGuid().ToString();
        await tenantRepository.InsertOneRtEntityAsync(session, x);

        await session.CommitTransactionAsync();

    }
}