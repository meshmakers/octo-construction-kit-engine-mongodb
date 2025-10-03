using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class BackupRestoreTests(SystemFixture systemFixture) : IClassFixture<SystemFixture>
{
    [Fact]
    public async Task Restore()
    {
        var repositoryOpsService = systemFixture.Provider.GetRequiredService<IRepositoryOpsService>();

        var filePath = "testData/backups/processautomationdemo.tar.gz";

        var commandResult = await repositoryOpsService.ExecuteMongoRestoreAsync(
            new MongoRestoreOptions
            {
                Drop = true,
                Archive = filePath,
                Database = "processautomationdemo",
                Gzip = true,
                Verbose = true
            }, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(commandResult.Success);
    }
}
