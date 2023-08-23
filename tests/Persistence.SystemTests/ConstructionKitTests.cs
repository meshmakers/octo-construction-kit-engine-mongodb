using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Persistence.IdentityCkModel;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class ConstructionKitTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;

    public ConstructionKitTests(SystemFixture systemFixture)
    {
        _systemFixture = systemFixture;
    }
    
    [Fact]
    public async void ImportConstructionKit()
    {
        var systemContext = _systemFixture.GetSystemContext();

        using var session = await systemContext.StartSystemSessionAsync();
        session.StartTransaction();
        var ckModelRepository = systemContext.CreateTenantCkModelRepository();
        var systemIdentityModelService = _systemFixture.Provider.GetRequiredService<CkSystemIdentityModelService>();
        //var systemIdentityModelService = new CkSystemIdentityModelService(new ImportCkModelCommand(logger, new CkJsonSerializer(), _systemFixture.CkModelValidator));

        await systemIdentityModelService.ImportAsync(session, ckModelRepository);

        await session.CommitTransactionAsync();
    }


}
