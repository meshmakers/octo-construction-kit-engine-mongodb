using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class ConfigurationFixture
{
    private readonly SystemTestConfiguration _configuration;

    public ConfigurationFixture()
    {
        _configuration = new SystemTestConfiguration();

        var serviceCollection = new ServiceCollection();

        serviceCollection.Configure<SystemTestOptions>(options =>
            _configuration.GetSection("systemTest").Bind(options));
        ServiceProvider = serviceCollection.BuildServiceProvider();
    }

    public ServiceProvider ServiceProvider { get; }

    public T GetOptions<T>(string sectionName)
    {
        var option = Activator.CreateInstance<T>();
        _configuration.GetSection(sectionName).Bind(option);
        return option;
    }
}
