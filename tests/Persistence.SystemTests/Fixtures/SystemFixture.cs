using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Testcontainers.MongoDb;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class SystemFixture : ConfigurationFixture, IAsyncLifetime
{
    protected readonly SystemTestOptions _options;
    private MongoDbContainer? _mongoDbContainer;
    private bool _isInitialized = false;

    public string SystemDatabaseName => "PersistenceSystemTests".ToLower();

    // ReSharper disable once MemberCanBeProtected.Global
    public SystemFixture()
    {
        _options = GetOptions<SystemTestOptions>("systemTest");
    }


    public ServiceProvider? Provider { get; private set; }

    public virtual async ValueTask InitializeAsync()
    {
        if (_isInitialized)
            return;

        // We need to create a new instance here to get the default admin user name
        var o = new OctoSystemConfiguration();

        // Start MongoDB test container with authentication
        _mongoDbContainer = new MongoDbBuilder()
            .WithImage("mongo:8.0")
            .WithReplicaSet()
            .WithName($"mongodb-test-{Guid.NewGuid():N}")
            .WithUsername(o.AdminUser)
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
            t.AdminUser = o.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = true; // For single-node replica set in tests
        });

        Provider = Services.BuildServiceProvider();

        // Initialize system tenant
        var systemContext = GetSystemContext();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (i == 0 && await systemContext.IsSystemTenantExistingAsync())
                {
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (await systemContext.IsSystemTenantExistingAsync())
                {
                    await Task.Delay(1000);
                    continue;
                }

                break;
            }
            catch (TenantException)
            {
                // do nothing here
            }
        }

        await systemContext.CreateSystemTenantAsync();
        _isInitialized = true;
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_mongoDbContainer != null)
        {
            await _mongoDbContainer.StopAsync();
            await _mongoDbContainer.DisposeAsync();
        }

        if (Provider is not null)
        {
            await Provider.DisposeAsync();
        }
    }

    public ISystemContext GetSystemContext()
    {
        if (Provider == null)
        {
            throw new InvalidOperationException("Provider is not initialized. Call InitializeAsync first.");
        }

        return Provider.GetRequiredService<ISystemContext>();
    }

    public T GetService<T>() where T : notnull
    {
        if (Provider == null)
        {
            throw new InvalidOperationException("Provider is not initialized. Call InitializeAsync first.");
        }

        return Provider.GetRequiredService<T>();
    }

    public string GetConnectionString()
    {
        if (_mongoDbContainer is null || !_isInitialized)
        {
            throw new InvalidOperationException("MongoDB container is not initialized. Call InitializeAsync first.");
        }
        
        return _mongoDbContainer.GetConnectionString();
    }
}
