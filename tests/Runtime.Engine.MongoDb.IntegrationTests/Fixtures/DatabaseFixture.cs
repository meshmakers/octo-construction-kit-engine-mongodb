using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

public class DatabaseFixture : ConfigurationFixture
{
    protected readonly SystemTestOptions _options;
    private MongoDbContainer? _mongoDbContainer;
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
            // Start MongoDB test container with authentication
            _mongoDbContainer = new MongoDbBuilder(_options.MongoDbImage)
                .WithReplicaSet()
                .WithName($"mongodb-test-{Guid.NewGuid():N}")
                .WithUsername(_options.AdminUser)
                .WithPassword(_options.AdminUserPassword)
                .Build();

            await _mongoDbContainer.StartAsync();

            var mappedPort = _mongoDbContainer.GetMappedPublicPort();
            databaseHost = $"localhost:{mappedPort}";
            Console.WriteLine($"Using Testcontainer MongoDB at {databaseHost}");
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

    protected override async Task DisposeServicesAsync()
    {
        await Task.Yield();
        if (_mongoDbContainer != null)
        {
            await _mongoDbContainer.StopAsync();
            await _mongoDbContainer.DisposeAsync();
        }
    }
}
