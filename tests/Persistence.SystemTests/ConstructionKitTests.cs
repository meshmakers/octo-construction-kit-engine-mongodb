using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
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
    public async Task ImportConstructionKit()
    {
        var systemContext = _systemFixture.GetSystemContext();

        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(new CkModelId("Test-1.0.0"), operationResult);
        //    await systemContext.ImportCkModelAsync(session, new CkModelId("System.Identity-1.0.0"), operationResult);

        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }
}