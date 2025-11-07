using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class SystemContextTests(DatabaseFixture databaseFixture)
    : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task IsSystemTenantExisting()
    {
        var systemContext = databaseFixture.GetSystemContext();
        var result = await systemContext.IsSystemTenantExistingAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task CreateAndDeleteSystemTenant()
    {
        var systemContext = databaseFixture.GetSystemContext();
        await systemContext.CreateSystemTenantAsync();
        var result = await systemContext.IsSystemTenantExistingAsync();
        Assert.True(result);
        await systemContext.DeleteSystemTenantAsync();
        result = await systemContext.IsSystemTenantExistingAsync();
        Assert.False(result);
    }


    [Fact]
    public async Task CreateAndImportCkModelTenant()
    {
        var systemContext = databaseFixture.GetSystemContext();
        try
        {
            await systemContext.CreateSystemTenantAsync();
            var result = await systemContext.IsSystemTenantExistingAsync();
            Assert.True(result);

            OperationResult operationResult = new();
            await systemContext.ImportCkModelAsync(TestCkIds.CkModelId, operationResult);
            Assert.False(operationResult.HasErrors);
            Assert.False(operationResult.HasFatalErrors);

        }
        finally
        {
            await systemContext.DeleteSystemTenantAsync();
            var result = await systemContext.IsSystemTenantExistingAsync();
            Assert.False(result);
        }
    }

}
