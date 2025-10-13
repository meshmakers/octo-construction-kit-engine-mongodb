using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ITestOutputHelper = Xunit.ITestOutputHelper;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class ServiceCollectionFixture : ITestOutputHelperAccessor
{
    public ServiceCollectionFixture()
    {
        Services = new ServiceCollection();
        Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository()
            .AddTenantComparison();
        Services.AddCkModelTest();
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Trace);
            loggingBuilder.AddXUnit(this);
        });
    }

    public ServiceCollection Services { get; }

    public ITestOutputHelper? OutputHelper { get; set; }
}


