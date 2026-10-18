using System;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#6968: the sweep grace period of a rollup recompute (<c>StreamData:Recompute:SweepGracePeriod</c>) defaults to two
/// hours, accepts zero (sweep at once) and refuses a negative value at start with a message that names the key.
/// </summary>
public class SweepGracePeriodOptionsTests
{
    private static ServiceProvider Provider(Action<IServiceCollection>? configure = null, params (string Key, string Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        configure?.Invoke(services);
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_IsTwoHours()
    {
        using var provider = Provider();

        Assert.Equal(TimeSpan.FromHours(2), provider.GetRequiredService<IOptions<CrateRecomputeOptions>>().Value.SweepGracePeriod);
        Assert.Equal(TimeSpan.FromHours(2), CrateRecomputeOptions.DefaultSweepGracePeriod);
    }

    [Fact]
    public void ConfiguredValue_IsBoundFromTheSection_ZeroMeansSweepAtOnce()
    {
        using var provider = Provider(null, ("StreamData:Recompute:SweepGracePeriod", "00:30:00"));
        Assert.Equal(TimeSpan.FromMinutes(30), provider.GetRequiredService<IOptions<CrateRecomputeOptions>>().Value.SweepGracePeriod);

        using var zero = Provider(null, ("StreamData:Recompute:SweepGracePeriod", "00:00:00"));
        Assert.Equal(TimeSpan.Zero, zero.GetRequiredService<IOptions<CrateRecomputeOptions>>().Value.SweepGracePeriod);
    }

    [Fact]
    public void NegativeValue_FailsAtStart_NamingTheKey()
    {
        using var provider = Provider(null, ("StreamData:Recompute:SweepGracePeriod", "-00:05:00"));

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("StreamData:Recompute:SweepGracePeriod", exception.Message, StringComparison.Ordinal);
        Assert.Contains("zero", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeValue_AlsoFailsWhenTheOptionsAreFirstRead()
    {
        using var provider = Provider(null, ("StreamData:Recompute:SweepGracePeriod", "-00:05:00"));

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CrateRecomputeOptions>>().Value);
    }

    [Fact]
    public void OtherInvalidValues_StillFail_WithTheSameMessage()
    {
        using var provider = Provider(null, ("StreamData:Recompute:SliceTargetRows", "0"));

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("SliceTargetRows", exception.Message, StringComparison.Ordinal);
    }

    private sealed class NoOpStreamDataConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public void Configure(string? name, StreamDataConfiguration options)
        {
        }

        public void Configure(StreamDataConfiguration options)
        {
        }
    }
}
