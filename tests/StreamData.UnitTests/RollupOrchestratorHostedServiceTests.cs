using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    // Generous ceiling for poll-until-tick waits. Local runs see the first tick within
    // a handful of milliseconds; CI agents under heavy load can stall the threadpool for
    // hundreds of milliseconds before the first scheduled tick fires. 5 seconds keeps the
    // tests deterministic without making green runs noticeably slower.
    private static readonly TimeSpan TickAssertionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Polls <paramref name="predicate"/> every <c>20ms</c> until it returns <c>true</c> or
    /// <paramref name="timeout"/> elapses. Returns silently in either case — the caller's
    /// follow-up FakeItEasy assertion is what surfaces a real failure with the expected
    /// diagnostic message. The polling avoids the historic <c>Task.Delay(80)</c> race where
    /// the hosted service hadn't ticked yet on slow CI agents (RollupOrchestratorHostedService
    /// has TickInterval=10ms but the threadpool can stall well past 80ms on a loaded box).
    /// </summary>
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

        // Wait until the hosted service has polled the tenant source at least twice — proves a
        // tick fired and saw the empty list. With TickInterval=10ms this is near-instant
        // locally and bounded by TickAssertionTimeout on slow CI agents. The follow-up
        // MustNotHaveHappened assertion then verifies the empty list short-circuited before
        // touching the system context — the actual contract under test.
        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Fake.GetCalls(_tenantSource).Count(c => c.Method.Name == "GetTenantIdsAsync") >= 2);
        await sut.StopAsync(CancellationToken.None);

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
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => WasCalled(tenantContext, "GetRollupOrchestrator"));
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
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => WasCalled(orchestratorA, "TickAsync") && WasCalled(orchestratorB, "TickAsync"));
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => orchestratorA.TickAsync(A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => orchestratorB.TickAsync(A<CancellationToken>._)).MustHaveHappened();
    }

    private (IRollupOrchestrator Orchestrator, IStreamDataRepository Repository) MarkedTenant(string tenantId, Func<bool> pending)
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { tenantId });
        var orchestrator = A.Fake<IRollupOrchestrator>();
        var repository = A.Fake<IStreamDataRepository>();
        A.CallTo(() => repository.IsRestoreReconcilePendingAsync(A<CancellationToken>._)).ReturnsLazily(() => Task.FromResult(pending()));
        var context = A.Fake<ITenantContext>();
        A.CallTo(() => context.GetRollupOrchestrator()).Returns(orchestrator);
        A.CallTo(() => context.GetStreamDataRepository()).Returns(repository);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(tenantId)).Returns(context);
        return (orchestrator, repository);
    }

    private static int Calls<T>(T fake, string methodName) where T : class =>
        Fake.GetCalls(fake).Count(c => c.Method.Name == methodName);

    [Fact]
    public async Task Tick_TenantMarkedRestoreReconcilePending_IsSkipped_NoForwardAggregation()
    {
        var (orchestrator, repository) = MarkedTenant("restored", () => true);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(repository, "IsRestoreReconcilePendingAsync") >= 3);
        await sut.StopAsync(CancellationToken.None);

        Assert.True(Calls(repository, "IsRestoreReconcilePendingAsync") >= 3);
        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Tick_TenantIsReleased_WhenTheMarkerIsCleared()
    {
        var pending = true;
        var (orchestrator, repository) = MarkedTenant("released", () => Volatile.Read(ref pending));

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(repository, "IsRestoreReconcilePendingAsync") >= 2);
        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustNotHaveHappened();
        Volatile.Write(ref pending, false);
        await WaitForTickAsync(() => Calls(orchestrator, "TickAsync") >= 1);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Tick_RepositoryWithoutTheMarker_IsNeverHeldBack()
    {
        var (orchestrator, repository) = MarkedTenant("legacy", () => false);
        A.CallTo(() => repository.IsRestoreReconcilePendingAsync(A<CancellationToken>._)).Throws(new ArchiveCopyNotSupportedException());

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(orchestrator, "TickAsync") >= 2);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustHaveHappened();
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
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => WasCalled(good, "TickAsync"));
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
