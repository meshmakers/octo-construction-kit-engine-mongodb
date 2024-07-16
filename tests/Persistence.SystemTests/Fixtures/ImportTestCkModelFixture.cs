using Meshmakers.Octo.ConstructionKit.Contracts;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class ImportTestCkModelFixture: SystemFixture, IAsyncLifetime
{
    public virtual async Task InitializeAsync()
    {
        var systemContext = GetSystemContext();
        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(new CkModelId("Test"), operationResult);
    }

    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}