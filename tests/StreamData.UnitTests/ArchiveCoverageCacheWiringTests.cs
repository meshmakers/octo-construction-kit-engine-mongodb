using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#5157 (wave 3, T5): the coverage memo is one process-wide <see cref="ArchiveCoverageCache"/>
/// shared by every tenant-scoped <see cref="CachedArchiveCoverageProvider"/>. <c>TenantContext</c>
/// itself cannot be constructed in a unit test (protected ctor over a live Mongo repository client,
/// metrics context and CK cache), so these tests pin the two halves it composes: the provider
/// sharing semantics on one cache with the injectable clock, and the DI registration both
/// stream-data entry points perform.
/// </summary>
public class ArchiveCoverageCacheWiringTests
{
    private static readonly OctoObjectId ArchiveRt = new("aa00000000000000000003a0");
    private static readonly ArchiveCoverage Measured = new(
        new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task TwoProvidersOfSameTenant_ShareOneCache_OtherTenantMisses()
    {
        var repository = A.Fake<IStreamDataRepository>();
        A.CallTo(() => repository.GetArchiveCoverageAsync(ArchiveRt, A<CancellationToken>._))
            .Returns(Measured);

        var now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        var cache = new ArchiveCoverageCache(TimeSpan.FromSeconds(60), () => now);

        // Two tenant-context-level providers for the same tenant (each TenantContext resolution
        // constructs its own provider around the shared singleton).
        var first = new CachedArchiveCoverageProvider("tenant-a", repository, cache);
        var second = new CachedArchiveCoverageProvider("tenant-a", repository, cache);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(Measured, await first.GetCoverageAsync(ArchiveRt, ct));
        Assert.Equal(Measured, await second.GetCoverageAsync(ArchiveRt, ct));
        A.CallTo(() => repository.GetArchiveCoverageAsync(ArchiveRt, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly(); // second call served from the cache

        // A different tenant id is a different key — it misses even for the same archive rtId.
        var otherTenant = new CachedArchiveCoverageProvider("tenant-b", repository, cache);
        Assert.Equal(Measured, await otherTenant.GetCoverageAsync(ArchiveRt, ct));
        A.CallTo(() => repository.GetArchiveCoverageAsync(ArchiveRt, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();

        // TTL expiry re-measures; a tenant-wide invalidation (the tenant-drop carry-over) does too.
        now = now.AddSeconds(61);
        await first.GetCoverageAsync(ArchiveRt, ct);
        A.CallTo(() => repository.GetArchiveCoverageAsync(ArchiveRt, A<CancellationToken>._))
            .MustHaveHappened(3, Times.Exactly);

        ((IArchiveCoverageInvalidator)cache).Invalidate("tenant-a");
        await second.GetCoverageAsync(ArchiveRt, ct);
        A.CallTo(() => repository.GetArchiveCoverageAsync(ArchiveRt, A<CancellationToken>._))
            .MustHaveHappened(4, Times.Exactly);
    }

    [Fact]
    public void AddStreamDataDatabase_RegistersOneCache_AsCacheAndInvalidator_WithDefaultTtl()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ArchiveCoverageCache>();
        var invalidator = provider.GetRequiredService<IArchiveCoverageInvalidator>();

        Assert.Same(cache, invalidator);
        Assert.Same(cache, provider.GetRequiredService<ArchiveCoverageCache>());
        Assert.Equal(ArchiveCoverageCache.DefaultCacheTtl, cache.CacheTtl);
    }

    [Fact]
    public void AddStreamDataDatabase_HonoursHostBoundCoverageOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // A host binds ArchiveCoverageOptions from StreamData:Coverage; here bound in code.
        services.Configure<ArchiveCoverageOptions>(o => o.CacheTtlSeconds = 5);
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ArchiveCoverageCache>();

        Assert.Equal(TimeSpan.FromSeconds(5), cache.CacheTtl);
    }

    [Fact]
    public void AddStreamDataDatabase_NegativeCoverageTtl_FailsFastNamingTheConfigurationKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ArchiveCoverageOptions>(o => o.CacheTtlSeconds = -1);
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();

        using var provider = services.BuildServiceProvider();

        // A misconfigured TTL must surface instead of silently reverting to the default.
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<ArchiveCoverageCache>());
        Assert.Contains("StreamData:Coverage:CacheTtlSeconds", exception.Message);
        Assert.Contains("-1", exception.Message);
    }

    [Fact]
    public void AddStreamDataDatabase_ZeroCoverageTtl_DisablesMemoisation()
    {
        // Zero is a legal setting (measure on every request), not a misconfiguration.
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ArchiveCoverageOptions>(o => o.CacheTtlSeconds = 0);
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.Zero, provider.GetRequiredService<ArchiveCoverageCache>().CacheTtl);
    }

    [Fact]
    public void AddStreamDataDatabase_KeepsAHostRegisteredCache()
    {
        var hostCache = new ArchiveCoverageCache(TimeSpan.FromSeconds(1));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(hostCache);
        services.AddStreamDataDatabase<NoOpStreamDataConfiguration>();

        using var provider = services.BuildServiceProvider();

        Assert.Same(hostCache, provider.GetRequiredService<ArchiveCoverageCache>());
        Assert.Same(hostCache, provider.GetRequiredService<IArchiveCoverageInvalidator>());
    }

    /// <summary>Satisfies the registration's options contract without touching a connection string.</summary>
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
