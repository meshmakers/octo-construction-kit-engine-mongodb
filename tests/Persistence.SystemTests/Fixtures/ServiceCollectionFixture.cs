using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class ServiceCollectionFixture
{
    public ServiceCollection Services { get; }

    public ServiceCollectionFixture()
    {
        Services = new ServiceCollection();
        Services.AddOctoPersistence();
        Services.AddOctoCommands();
        Services.AddCkModelTest();
        Services.AddCkModelSystemIdentity();
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Trace);
        });
    }

    

}