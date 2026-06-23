using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(SystemCollection.Name)]
public class ConstructionKitTests(SystemFixture systemFixture)
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
