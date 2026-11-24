using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8875 AC1: the limit of concurrent insert statements can be configured. The key
/// <c>StreamData:MaxConcurrentInsertStatements</c> is part of the <c>StreamData</c> section the engine binds into
/// <see cref="StreamDataInstanceConfiguration" />, and the stream data client takes its limit from there. The key
/// <c>StreamData:MaxPoolSize</c> of the same section sets the pool of the tenant's data source and the upper bound of the
/// limit; without it the host's pool size applies (default 32).
/// </summary>
public class MaxConcurrentInsertStatementsBindingTests
{
    private const string EnvironmentVariable = "OCTO_STREAMDATA__MAXCONCURRENTINSERTSTATEMENTS";

    [Fact]
    public void ConfiguredKey_IsBoundIntoTheInstanceConfiguration()
    {
        var instance = Bind(new ConfigurationBuilder()
            .AddInMemoryCollection([new("StreamData:MaxConcurrentInsertStatements", "2")])
            .Build());

        Assert.Equal(2, instance.MaxConcurrentInsertStatements);
    }

    [Fact]
    public void MissingKey_KeepsTheDefaultOfFour()
    {
        var instance = Bind(new ConfigurationBuilder()
            .AddInMemoryCollection([new("StreamData:Enabled", "true")])
            .Build());

        Assert.Equal(4, instance.MaxConcurrentInsertStatements);
    }

    [Fact]
    public void EnvironmentVariableOfTheServices_IsBoundIntoTheInstanceConfiguration()
    {
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, "3");
        try
        {
            var instance = Bind(new ConfigurationBuilder().AddEnvironmentVariables("OCTO_").Build());

            Assert.Equal(3, instance.MaxConcurrentInsertStatements);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task ConfiguredKey_IsTheLimitOfTheClient()
    {
        await using var provider = EngineProvider(("StreamData:MaxConcurrentInsertStatements", "2"));

        Assert.Equal(2, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Fact]
    public async Task MissingKey_TheClientWritesFourStatementsAtATime()
    {
        await using var provider = EngineProvider();

        Assert.Equal(4, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Fact]
    public async Task ValueAboveThePoolSize_IsClampedToThePool()
    {
        await using var provider = EngineProvider(("StreamData:MaxConcurrentInsertStatements", "50"));

        Assert.Equal(32, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Fact]
    public async Task PoolSizeKey_ReachesTheDataSourceOfTheRegisteredConnectionAccess()
    {
        await using var provider = EngineProvider(("StreamData:MaxPoolSize", "8"));

        Assert.Equal(8, PoolSizeOfTheDataSource(provider));
    }

    [Fact]
    public async Task PoolSizeKey_ClampsTheConcurrentInsertStatements()
    {
        await using var provider = EngineProvider(
            ("StreamData:MaxPoolSize", "8"),
            ("StreamData:MaxConcurrentInsertStatements", "20"));

        Assert.Equal(8, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Fact]
    public async Task MissingPoolSizeKey_KeepsAPoolOf32()
    {
        await using var provider = EngineProvider(("StreamData:MaxConcurrentInsertStatements", "50"));

        Assert.Equal(32, PoolSizeOfTheDataSource(provider));
        Assert.Equal(32, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task PoolSizeKeyNotPositive_KeepsAPoolOf32(string value)
    {
        await using var provider = EngineProvider(
            ("StreamData:MaxPoolSize", value),
            ("StreamData:MaxConcurrentInsertStatements", "50"));

        Assert.Equal(32, PoolSizeOfTheDataSource(provider));
        Assert.Equal(32, provider.GetRequiredService<CrateDatabaseClient>().MaxConcurrentInsertStatements);
    }

    [Fact]
    public async Task PoolSizeKey_OverridesThePoolSizeOfTheHost_AndWithoutItTheHostsValueApplies()
    {
        await using var withKey = EngineProvider<HostWithPoolOfSix>(("StreamData:MaxPoolSize", "8"));
        await using var withoutKey = EngineProvider<HostWithPoolOfSix>();

        Assert.Equal(8, PoolSizeOfTheDataSource(withKey));
        Assert.Equal(6, PoolSizeOfTheDataSource(withoutKey));
    }

    [Fact]
    public void PoolSizeEnvironmentVariableOfTheServices_IsBoundIntoTheInstanceConfiguration()
    {
        const string variable = "OCTO_STREAMDATA__MAXPOOLSIZE";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "8");
        try
        {
            var instance = Bind(new ConfigurationBuilder().AddEnvironmentVariables("OCTO_").Build());

            Assert.Equal(8, instance.MaxPoolSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static int PoolSizeOfTheDataSource(IServiceProvider provider)
    {
        var access = Assert.IsType<CrateDbConnectionAccess>(provider.GetRequiredService<ICrateDbConnectionAccess>());
        return new NpgsqlConnectionStringBuilder(access.ConnectionString).MaxPoolSize;
    }

    private static StreamDataInstanceConfiguration Bind(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddOptions<StreamDataInstanceConfiguration>()
            .BindConfiguration(StreamDataInstanceConfiguration.SectionName);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<StreamDataInstanceConfiguration>>().Value;
    }

    private static ServiceProvider EngineProvider(params (string Key, string Value)[] settings)
        => EngineProvider<HostConfiguration>(settings);

    private static ServiceProvider EngineProvider<THost>(params (string Key, string Value)[] settings)
        where THost : class, IConfigureNamedOptions<StreamDataConfiguration>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

        var builder = A.Fake<IRuntimeEngineBuilder>();
        A.CallTo(() => builder.Services).Returns(services);
        builder.AddCrateDbStreamDataRepository<THost>();

        return services.BuildServiceProvider();
    }

    private sealed class HostConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public void Configure(string? name, StreamDataConfiguration options)
            => options.ConnectionString = "Host=crate.example;Username=octo";

        public void Configure(StreamDataConfiguration options) => Configure(Options.DefaultName, options);
    }

    private sealed class HostWithPoolOfSix : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public void Configure(string? name, StreamDataConfiguration options)
        {
            options.ConnectionString = "Host=crate.example;Username=octo";
            options.MaxPoolSize = 6;
        }

        public void Configure(StreamDataConfiguration options) => Configure(Options.DefaultName, options);
    }
}
