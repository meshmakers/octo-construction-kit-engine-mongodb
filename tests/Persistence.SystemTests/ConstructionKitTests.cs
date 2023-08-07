using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Backend.Persistence.SystemTests.CkModelEntities;
using Meshmakers.Octo.Backend.Persistence.SystemTests.Fixtures;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using MongoDB.Bson;
using Persistence.IdentityCkModel;
using Xunit;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

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
        var systemIdentityModelService = new CkSystemIdentityModelService(new ImportCkModelCommand());

        await systemIdentityModelService.ImportAsync(session, ckModelRepository);

        await session.CommitTransactionAsync();
    }


}
