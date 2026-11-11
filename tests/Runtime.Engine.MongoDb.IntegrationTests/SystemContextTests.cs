using System.Runtime.ExceptionServices;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class SystemContextTests(DatabaseFixture databaseFixture)
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
        ExceptionDispatchInfo? failure = null;
        try
        {
            await systemContext.CreateSystemTenantAsync();
            var result = await systemContext.IsSystemTenantExistingAsync();
            Assert.True(result);

            OperationResult operationResult = new();
            await systemContext.ImportCkModelAsync(TestCkIds.CkModelId, operationResult);
            Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages.Select(m => $"{m.MessageNumber}: {m.MessageText}")));
            Assert.False(operationResult.HasFatalErrors);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        // The cleanup must never hide why the test failed: a failed create rolls the system database back, and the
        // delete below then only reports that it does not exist (CI 68737).
        Exception? cleanupFailure = null;
        try
        {
            await systemContext.DeleteSystemTenantAsync();
            Assert.False(await systemContext.IsSystemTenantExistingAsync());
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
        }

        if (failure != null && cleanupFailure != null)
        {
            throw new AggregateException("The test failed, and so did its cleanup (the first exception is the cause)",
                failure.SourceException, cleanupFailure);
        }

        failure?.Throw();
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Throw(cleanupFailure);
        }
    }
}
