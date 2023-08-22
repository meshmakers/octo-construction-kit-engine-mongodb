using FakeItEasy;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Microsoft.Extensions.Logging;
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
        var logger = A.Fake<ILogger<ImportCkModelCommand>>();

        using var session = await systemContext.StartSystemSessionAsync();
        session.StartTransaction();
        var ckModelRepository = systemContext.CreateTenantCkModelRepository();
        var systemIdentityModelService = new CkSystemIdentityModelService(new ImportCkModelCommand(logger, new CkJsonSerializer(), _systemFixture.CkModelValidator));

        await systemIdentityModelService.ImportAsync(session, ckModelRepository);

        await session.CommitTransactionAsync();
    }


}
