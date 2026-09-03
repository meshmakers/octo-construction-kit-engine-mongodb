using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

public class DatabaseFixture : ConfigurationFixture
{
    protected readonly SystemTestOptions _options;
    private bool _useLocalDatabase;

    public DatabaseFixture()
    {
        _options = GetOptions<SystemTestOptions>("systemTest");

        // Check environment variable first, then fall back to config
        var envVar = Environment.GetEnvironmentVariable("USE_LOCAL_MONGODB");
        _useLocalDatabase = !string.IsNullOrEmpty(envVar) &&
                            (envVar.Equals("true", StringComparison.OrdinalIgnoreCase) || envVar == "1")
                            || _options.UseLocalDatabase;
    }

    protected override async Task InitializeServicesAsync()
    {
        string databaseHost;

        if (_useLocalDatabase)
        {
            // Use local MongoDB instance
            databaseHost = _options.LocalDatabaseHost;
            Console.WriteLine($"Using local MongoDB at {databaseHost}");
        }
        else
        {
            // Every fixture now has its own SystemDatabaseName (GUID-suffixed), so fixtures no
            // longer need a private server to avoid colliding on the same database. Share one
            // MongoDB Testcontainer for the whole test process instead of starting one per fixture.
            databaseHost = await SharedMongoDbContainer.GetHostAsync(_options);
        }

        // Configure services with the connection
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = SystemDatabaseName;
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = _useLocalDatabase ? _options.UseDirectConnection : true;
        });

        await base.InitializeServicesAsync();
    }

    protected override Task DisposeServicesAsync()
    {
        // The shared container (SharedMongoDbContainer) outlives every individual fixture and is
        // torn down by Testcontainers' Ryuk reaper when the test process exits, not here.
        return Task.CompletedTask;
    }
}
