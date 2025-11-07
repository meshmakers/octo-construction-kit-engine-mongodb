using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class ConstructionKitTests(SystemFixture systemFixture) : IClassFixture<SystemFixture>
{
    [Fact]
    public async Task ImportConstructionKit()
    {
        var systemContext = systemFixture.GetSystemContext();

        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(new CkModelId("Test-1.0.0"), operationResult);

        var r = await systemContext.IsCkModelExistingAsync(new CkModelId("Test-1.0.0"));

        Assert.True(r);
        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
    }
}
