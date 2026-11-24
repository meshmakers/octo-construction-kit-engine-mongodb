using FakeItEasy;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8875 AC1: the limit of concurrent insert statements can be configured. A host registers the stream data client with
/// its own <see cref="IConfigureNamedOptions{TOptions}" /> (connection string, pool); the key
/// <c>StreamData:MaxConcurrentInsertStatements</c> must reach the client on top of that, and only that key.
/// </summary>
public class MaxConcurrentInsertStatementsBindingTests
{
    private const string HostConnectionString = "Host=crate.example;Username=octo";

    public static TheoryData<string> Registrations => new() { "builder", "services" };

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task ConfiguredValue_IsTheLimitOfTheClient(string registration)
    {
        await using var provider = Provider(registration, ("StreamData:MaxConcurrentInsertStatements", "2"));

        Assert.Equal(2, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task MissingKey_KeepsTheDefaultOfFour(string registration)
    {
        await using var provider = Provider(registration);

        Assert.Equal(4, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task ValueAboveThePoolSize_IsClampedToThePool(string registration)
    {
        await using var provider = Provider(registration, ("StreamData:MaxConcurrentInsertStatements", "50"));

        Assert.Equal(HostConfiguration.HostMaxPoolSize,
            provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task OtherStreamDataSettings_StayAsTheHostSetThem(string registration)
    {
        await using var provider = Provider(registration,
            ("StreamData:MaxConcurrentInsertStatements", "2"),
            ("StreamData:MaxPoolSize", "99"),
            ("StreamData:ConnectionString", "Host=from-configuration"),
            ("StreamData:NumberOfShards", "7"));

        var options = provider.GetRequiredService<IOptions<StreamDataConfiguration>>().Value;

        Assert.Equal(2, options.MaxConcurrentInsertStatements);
        Assert.Equal(HostConnectionString, options.ConnectionString);
        Assert.Equal(HostConfiguration.HostMaxPoolSize, options.MaxPoolSize);
        Assert.Equal(3, options.NumberOfShards);
    }

    private static ServiceProvider Provider(string registration, params (string Key, string Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

        if (registration == "builder")
        {
            var builder = A.Fake<IRuntimeEngineBuilder>();
            A.CallTo(() => builder.Services).Returns(services);
            builder.AddCrateDbStreamDataRepository<HostConfiguration>();
        }
        else
        {
            services.AddStreamDataDatabase<HostConfiguration>();
        }

        return services.BuildServiceProvider();
    }

    private sealed class HostConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public const int HostMaxPoolSize = 6;

        public void Configure(string? name, StreamDataConfiguration options)
        {
            options.ConnectionString = HostConnectionString;
            options.MaxPoolSize = HostMaxPoolSize;
        }

        public void Configure(StreamDataConfiguration options) => Configure(Options.DefaultName, options);
    }
}
