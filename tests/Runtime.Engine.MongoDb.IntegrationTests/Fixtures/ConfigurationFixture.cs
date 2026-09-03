using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

public abstract class ConfigurationFixture : ServiceCollectionFixture
{
    private readonly SystemTestConfiguration _configuration;

    // Unique per fixture instance so unrelated fixtures can share one MongoDB server
    // (SharedMongoDbContainer) without colliding on the same database.
    public string SystemDatabaseName { get; } = $"persistencesystemtests{Guid.NewGuid():N}";

    protected ConfigurationFixture()
    {
        _configuration = new SystemTestConfiguration();

        Services.Configure<SystemTestOptions>(options =>
            _configuration.GetSection("systemTest").Bind(options));
    }

    protected T GetOptions<T>(string sectionName)
    {
        var option = Activator.CreateInstance<T>();
        _configuration.GetSection(sectionName).Bind(option);
        return option;
    }
}
