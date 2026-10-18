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

    /// <summary>A tenant whose repository answers the restore reconcile marker with <paramref name="pending"/>.</summary>
    private (IRecomputeOrchestrator Orchestrator, IStreamDataRepository Repository) MarkedTenant(string tenantId, Func<bool> pending)
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { tenantId });
        var orchestrator = A.Fake<IRecomputeOrchestrator>();
        var repository = A.Fake<IStreamDataRepository>();
        A.CallTo(() => repository.IsRestoreReconcilePendingAsync(A<CancellationToken>._)).ReturnsLazily(() => Task.FromResult(pending()));
        A.CallTo(() => repository.ValidateRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<CancellationToken>._))
            .Returns(new RollupConsistencyReport([], DateTime.UtcNow));
        var context = A.Fake<ITenantContext>();
        A.CallTo(() => context.GetRecomputeOrchestrator()).Returns(orchestrator);
        A.CallTo(() => context.GetStreamDataRepository()).Returns(repository);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(tenantId)).Returns(context);
        return (orchestrator, repository);
    }

    private static int Calls<T>(T fake, string methodName) where T : class =>
        Fake.GetCalls(fake).Count(c => c.Method.Name == methodName);

    [Fact]
    public async Task Tick_TenantMarkedRestoreReconcilePending_IsSkipped_NoRecomputeAndNoSweep()
    {
        var (orchestrator, repository) = MarkedTenant("restored", () => true);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(repository, "IsRestoreReconcilePendingAsync") >= 3);
        await sut.StopAsync(CancellationToken.None);

        Assert.True(Calls(repository, "IsRestoreReconcilePendingAsync") >= 3);
        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => repository.SweepSupersededGenerationsAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Tick_TenantWithoutTheMarker_RecomputesAndSweeps()
    {
        var (orchestrator, repository) = MarkedTenant("normal", () => false);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(orchestrator, "TickAsync") >= 2 && Calls(repository, "SweepSupersededGenerationsAsync") >= 2);
        await sut.StopAsync(CancellationToken.None);

        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => repository.SweepSupersededGenerationsAsync(A<CancellationToken>._)).MustHaveHappened();
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
    public async Task Tick_ValidatesTheRollupsOncePerTenantAtStart_EvenWhileTheTenantIsMarked()
    {
        var (_, repository) = MarkedTenant("restored", () => true);

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(repository, "IsRestoreReconcilePendingAsync") >= 4);
        await sut.StopAsync(CancellationToken.None);

        Assert.True(Calls(repository, "IsRestoreReconcilePendingAsync") >= 4);
        A.CallTo(() => repository.ValidateRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Tick_AFailingValidationAtStart_IsTriedAgainAndNeverStopsTheTick()
    {
        var (orchestrator, repository) = MarkedTenant("flaky", () => false);
        A.CallTo(() => repository.ValidateRollupsAsync(A<IReadOnlyCollection<OctoObjectId>?>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("CrateDB unavailable"));

        var sut = NewSut();
        await sut.StartAsync(CancellationToken.None);
        await WaitForTickAsync(() => Calls(repository, "ValidateRollupsAsync") >= 2 && Calls(orchestrator, "TickAsync") >= 2);
        await sut.StopAsync(CancellationToken.None);

        Assert.True(Calls(repository, "ValidateRollupsAsync") >= 2);
        A.CallTo(() => orchestrator.TickAsync(A<CancellationToken>._)).MustHaveHappened();
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
