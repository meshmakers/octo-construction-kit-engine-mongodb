using Meshmakers.Octo.ConstructionKit.Contracts;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class ImportTestCkModelFixture : SystemFixture
{
    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(new CkModelId("Test"), operationResult);
    }

    public async Task ClearCollectionAsync()
    {
        var systemContext = GetSystemContext();
        await systemContext.ClearSystemTenantAsync();
        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(new CkModelId("Test"), operationResult);
    }
}
