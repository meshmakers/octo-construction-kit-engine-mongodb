using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RollupOrchestratorHostedServiceTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IRollupTenantSource _tenantSource = A.Fake<IRollupTenantSource>();

    private static IOptionsMonitor<RollupOrchestratorOptions> Options(TimeSpan? tick = null, TimeSpan? startup = null)
    {
        var value = new RollupOrchestratorOptions
        {
            TickInterval = tick ?? TimeSpan.FromMilliseconds(10),
            StartupDelay = startup ?? TimeSpan.Zero,
        };
        var monitor = A.Fake<IOptionsMonitor<RollupOrchestratorOptions>>();
        A.CallTo(() => monitor.CurrentValue).Returns(value);
        return monitor;
    }

    private RollupOrchestratorHostedService NewSut(TimeSpan? tick = null, TimeSpan? startup = null) =>
        new(_systemContext, _tenantSource, Options(tick, startup),
            NullLogger<RollupOrchestratorHostedService>.Instance);

    [Fact]
    public async Task Tick_NoTenants_NoOps()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._))
            .Returns(Array.Empty<string>());

        // Start, wait long enough for at least one tick, then stop.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await NewSut().StartAsync(cts.Token); await Task.Delay(50, cts.Token); }
        catch (OperationCanceledException) { /* expected on cancel */ }
        await NewSut().StopAsync(CancellationToken.None);

        A.CallTo(() => _systemContext.TryFindTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Tick_TenantWithoutOrchestrator_Skipped()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._))
            .Returns(new[] { "tenant-x" });

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetRollupOrchestrator()).Returns((IRollupOrchestrator?)null);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-x")).Returns(tenantContext);

        var sut = NewSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await sut.StartAsync(cts.Token);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => tenantContext.GetRollupOrchestrator()).MustHaveHappened();
    }

    [Fact]
    public async Task Tick_DrivesEveryTenantsOrchestrator()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._))
            .Returns(new[] { "tenant-a", "tenant-b" });

        var orchestratorA = A.Fake<IRollupOrchestrator>();
        var orchestratorB = A.Fake<IRollupOrchestrator>();
        A.CallTo(() => orchestratorA.TickAsync(A<CancellationToken>._)).Returns(3);
        A.CallTo(() => orchestratorB.TickAsync(A<CancellationToken>._)).Returns(0);

        var contextA = A.Fake<ITenantContext>();
        var contextB = A.Fake<ITenantContext>();
        A.CallTo(() => contextA.GetRollupOrchestrator()).Returns(orchestratorA);
        A.CallTo(() => contextB.GetRollupOrchestrator()).Returns(orchestratorB);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-a")).Returns(contextA);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-b")).Returns(contextB);

        var sut = NewSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await sut.StartAsync(cts.Token);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => orchestratorA.TickAsync(A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => orchestratorB.TickAsync(A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Tick_OneTenantThrows_DoesNotStopOthers()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._))
            .Returns(new[] { "tenant-bad", "tenant-good" });

        var bad = A.Fake<IRollupOrchestrator>();
        A.CallTo(() => bad.TickAsync(A<CancellationToken>._)).Throws<InvalidOperationException>();
        var good = A.Fake<IRollupOrchestrator>();
        A.CallTo(() => good.TickAsync(A<CancellationToken>._)).Returns(1);

        var badContext = A.Fake<ITenantContext>();
        var goodContext = A.Fake<ITenantContext>();
        A.CallTo(() => badContext.GetRollupOrchestrator()).Returns(bad);
        A.CallTo(() => goodContext.GetRollupOrchestrator()).Returns(good);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-bad")).Returns(badContext);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-good")).Returns(goodContext);

        var sut = NewSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await sut.StartAsync(cts.Token);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => good.TickAsync(A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ConfigBasedTenantSource_ReturnsConfiguredIds()
    {
        var options = A.Fake<IOptionsMonitor<RollupOrchestratorOptions>>();
        A.CallTo(() => options.CurrentValue)
            .Returns(new RollupOrchestratorOptions { TenantIds = new[] { "alpha", "beta" } });
        var source = new ConfigBasedRollupTenantSource(options);

        var ids = await source.GetTenantIdsAsync(CancellationToken.None);

        Assert.Equal(new[] { "alpha", "beta" }, ids);
    }
}
