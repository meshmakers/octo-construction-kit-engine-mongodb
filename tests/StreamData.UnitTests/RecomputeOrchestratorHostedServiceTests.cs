using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RecomputeOrchestratorHostedServiceTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IRollupTenantSource _tenantSource = A.Fake<IRollupTenantSource>();

    private static readonly TimeSpan TickAssertionTimeout = TimeSpan.FromSeconds(5);

    private static async Task WaitForTickAsync(Func<bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TickAssertionTimeout)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(20);
        }
    }

    private static bool WasCalled<T>(T fake, string methodName) where T : class =>
        Fake.GetCalls(fake).Any(c => c.Method.Name == methodName);

    private static IOptionsMonitor<RecomputeOrchestratorOptions> Options()
    {
        var value = new RecomputeOrchestratorOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            StartupDelay = TimeSpan.Zero,
        };
        var monitor = A.Fake<IOptionsMonitor<RecomputeOrchestratorOptions>>();
        A.CallTo(() => monitor.CurrentValue).Returns(value);
        return monitor;
    }

    private RecomputeOrchestratorHostedService NewSut() =>
        new(_systemContext, _tenantSource, Options(),
            NullLogger<RecomputeOrchestratorHostedService>.Instance);

    [Fact]
    public async Task Tick_NoTenants_NoOps()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(Array.Empty<string>());

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Fake.GetCalls(_tenantSource).Count(c => c.Method.Name == "GetTenantIdsAsync") >= 2);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => _systemContext.TryFindTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Tick_TenantWithoutOrchestrator_Skipped()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "tenant-x" });

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetRecomputeOrchestrator()).Returns((IRecomputeOrchestrator?)null);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-x")).Returns(tenantContext);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => WasCalled(tenantContext, "GetRecomputeOrchestrator"));
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => tenantContext.GetRecomputeOrchestrator()).MustHaveHappened();
    }

    [Fact]
    public async Task Tick_DrivesEveryTenantsOrchestrator()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "tenant-a", "tenant-b" });

        var orchestratorA = A.Fake<IRecomputeOrchestrator>();
        var orchestratorB = A.Fake<IRecomputeOrchestrator>();
        A.CallTo(() => orchestratorA.TickAsync(A<CancellationToken>._)).Returns(2);
        A.CallTo(() => orchestratorB.TickAsync(A<CancellationToken>._)).Returns(0);

        var contextA = A.Fake<ITenantContext>();
        var contextB = A.Fake<ITenantContext>();
        A.CallTo(() => contextA.GetRecomputeOrchestrator()).Returns(orchestratorA);
        A.CallTo(() => contextB.GetRecomputeOrchestrator()).Returns(orchestratorB);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-a")).Returns(contextA);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-b")).Returns(contextB);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => WasCalled(orchestratorA, "TickAsync") && WasCalled(orchestratorB, "TickAsync"));
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => orchestratorA.TickAsync(A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => orchestratorB.TickAsync(A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Tick_OneTenantThrows_DoesNotStopOthers()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "tenant-bad", "tenant-good" });

        var bad = A.Fake<IRecomputeOrchestrator>();
        A.CallTo(() => bad.TickAsync(A<CancellationToken>._)).Throws<InvalidOperationException>();
        var good = A.Fake<IRecomputeOrchestrator>();
        A.CallTo(() => good.TickAsync(A<CancellationToken>._)).Returns(1);

        var badContext = A.Fake<ITenantContext>();
        var goodContext = A.Fake<ITenantContext>();
        A.CallTo(() => badContext.GetRecomputeOrchestrator()).Returns(bad);
        A.CallTo(() => goodContext.GetRecomputeOrchestrator()).Returns(good);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-bad")).Returns(badContext);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("tenant-good")).Returns(goodContext);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => WasCalled(good, "TickAsync"));
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => good.TickAsync(A<CancellationToken>._)).MustHaveHappened();
    }
}
