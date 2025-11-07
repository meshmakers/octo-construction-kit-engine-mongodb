using Microsoft.Extensions.Configuration;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

public class SystemTestConfiguration
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.test.json", true)
        .Build();

    public IConfigurationSection GetSection(string section)
    {
        return _configuration.GetSection(section);
    }
}
