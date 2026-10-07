using System.Diagnostics;

using MartinCostello.Logging.XUnit;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

using ITestOutputHelper = Xunit.ITestOutputHelper;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

public abstract class ServiceCollectionFixture : ITestOutputHelperAccessor, IAsyncLifetime
{
    private bool _isInitialized;

    public ServiceCollectionFixture()
    {
        Services = new ServiceCollection();
        Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();
        Services.AddCkModelTestV1();

        // AB#4924 — the tenant-location seam, registered always and answering never until a test arms
        // it (see TestTenantLocationSource). Registering it here rather than per test is what lets a
        // test exercise the detached resolve through the real container; it changes nothing for every
        // other test because an unarmed source declines and the registry route runs unchanged.
        Services.AddSingleton<TestTenantLocationSource>();
        Services.AddSingleton<ITenantLocationSource>(sp => sp.GetRequiredService<TestTenantLocationSource>());
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

    /// <summary>
    ///     Tears the fixture down step by step, each step bounded and reported (AB#5436). Neither the
    ///     fixture's own cleanup nor the service provider — whose singletons close MongoDB connections
    ///     and, for a throwaway tenant, may still be talking to a database that is on its way out — may
    ///     hold the run open or turn a finished run into a cleanup failure. Whatever the tests decided
    ///     has already been reported by the time we get here.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var name = GetType().Name;

        await RunProgress.RunBoundedAsync($"{name} teardown", DisposeServicesAsync);

        if (Provider is not null)
        {
            var provider = Provider;
            await RunProgress.RunBoundedAsync($"{name} service provider dispose",
                async () => await provider.DisposeAsync());
        }

        RunProgress.FixtureTornDown();
    }

    public async ValueTask InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        // Reported because this is where the suite's wall clock actually goes: every collection
        // fixture creates its own system tenant and imports the CK models, which xUnit attributes to
        // no test at all, so it is invisible both in the console output and in the .trx durations.
        var name = GetType().Name;
        var watch = Stopwatch.StartNew();
        RunProgress.Report($"{name}: initialising");
        try
        {
            await InitializeServicesAsync();
        }
        catch (Exception ex)
        {
            RunProgress.Report(
                $"{name}: initialisation FAILED after {RunProgress.Seconds(watch.Elapsed)}s — {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        RunProgress.Report($"{name}: ready in {RunProgress.Seconds(watch.Elapsed)}s");
        RunProgress.FixtureReady();
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


