using Meshmakers.Octo.ConstructionKit.Contracts;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class ImportTestCkModelFixture : SystemFixture, IAsyncLifetime
{
    public virtual async ValueTask InitializeAsync()
    {
        var systemContext = GetSystemContext();
        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(new CkModelId("Test"), operationResult);
    }

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
    
    public async Task ClearCollectionAsync()
    {
        var systemContext = GetSystemContext();
        await systemContext.ClearSystemTenantAsync();
        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(new CkModelId("Test"), operationResult);
    }
}
