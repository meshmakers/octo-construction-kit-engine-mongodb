using MartinCostello.Logging.XUnit;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

using ITestOutputHelper = Xunit.ITestOutputHelper;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public abstract class ServiceCollectionFixture : ITestOutputHelperAccessor, IAsyncLifetime
{
    private bool _isInitialized;

    public ServiceCollectionFixture()
    {
        Services = new ServiceCollection();
        Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();
        Services.AddCkModelTest();
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Trace);
            loggingBuilder.AddXUnit(this);
        });
    }

    public ServiceCollection Services { get; }

    public ServiceProvider? Provider { get; private set; }

    public ITestOutputHelper? OutputHelper { get; set; }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Fixture is not initialized. Call InitializeAsync first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeServicesAsync();

        if (Provider is not null)
        {
            await Provider.DisposeAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await InitializeServicesAsync();

    }

    protected virtual Task InitializeServicesAsync()
    {
        Provider = Services.BuildServiceProvider();
        _isInitialized = true;

        return Task.CompletedTask;
    }

    protected abstract Task DisposeServicesAsync();

    public T GetService<T>() where T : notnull
    {
        if (Provider == null)
        {
            throw new InvalidOperationException("Provider is not initialized. Call InitializeAsync first.");
        }

        return Provider.GetRequiredService<T>();
    }

    public ISystemContext GetSystemContext()
    {
        if (Provider == null)
        {
            throw new InvalidOperationException("Provider is not initialized. Call InitializeAsync first.");
        }

        return Provider.GetRequiredService<ISystemContext>();
    }
}


