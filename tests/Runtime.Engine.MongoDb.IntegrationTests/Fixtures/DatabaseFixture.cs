using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

public class DatabaseFixture : ConfigurationFixture
{
    protected readonly SystemTestOptions _options;
    private MongoDbContainer? _mongoDbContainer;

    public DatabaseFixture()
    {
        _options = GetOptions<SystemTestOptions>("systemTest");
    }

    protected override async Task InitializeServicesAsync()
    {
        // Start MongoDB test container with authentication
        _mongoDbContainer = new MongoDbBuilder()
            .WithImage(_options.MongoDbImage)
            .WithReplicaSet()
            .WithName($"mongodb-test-{Guid.NewGuid():N}")
            .WithUsername(_options.AdminUser)
            .WithPassword(_options.AdminUserPassword)
            .Build();

        await _mongoDbContainer.StartAsync();

        var mappedPort = _mongoDbContainer.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";

        // Configure services with the test container connection
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = SystemDatabaseName;
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = true; // For single-node replica set in tests
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


    // public string GetConnectionString()
    // {
    //     EnsureInitialized();
    //
    //     if (_mongoDbContainer is null)
    //     {
    //         throw new InvalidOperationException("MongoDB container is not initialized. Call InitializeAsync first.");
    //     }
    //
    //     return _mongoDbContainer.GetConnectionString();
    // }
}
