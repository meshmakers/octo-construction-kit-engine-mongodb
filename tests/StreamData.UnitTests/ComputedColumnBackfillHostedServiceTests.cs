using System;
using System.Collections.Generic;
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

/// <summary>
/// AB#8002: the background service that runs the queued computed-column backfills, outside any
/// request, tenant after tenant.
/// </summary>
public class ComputedColumnBackfillHostedServiceTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IRollupTenantSource _tenantSource = A.Fake<IRollupTenantSource>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ComputedColumnBackfillHostedService NewSut()
    {
        var monitor = A.Fake<IOptionsMonitor<ComputedColumnBackfillOptions>>();
        A.CallTo(() => monitor.CurrentValue).Returns(new ComputedColumnBackfillOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10), StartupDelay = TimeSpan.Zero,
        });
        return new ComputedColumnBackfillHostedService(_systemContext, _tenantSource, monitor,
            NullLogger<ComputedColumnBackfillHostedService>.Instance);
    }

    private IArchiveLifecycleService Tenant(string tenantId)
    {
        var context = A.Fake<ITenantContext>();
        var lifecycle = A.Fake<IArchiveLifecycleService>();
        A.CallTo(() => context.GetArchiveLifecycleService()).Returns(lifecycle);
        A.CallTo(() => context.GetRecomputeOrchestrator()).Returns(A.Fake<IRecomputeOrchestrator>());
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(tenantId)).Returns(context);
        return lifecycle;
    }

    [Fact]
    public async Task Drain_RunsEveryTenantsQueue_WithTheHostToken()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "a", "b" });
        var a = Tenant("a");
        var b = Tenant("b");
        using var host = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await NewSut().DrainAllTenantsAsync(host.Token);

        A.CallTo(() => a.RunPendingComputedColumnBackfillsAsync(host.Token)).MustHaveHappenedOnceExactly();
        A.CallTo(() => b.RunPendingComputedColumnBackfillsAsync(host.Token)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Drain_TenantsRunOneAfterTheOther()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "a", "b" });
        var running = 0;
        var maxParallel = 0;
        foreach (var lifecycle in new[] { Tenant("a"), Tenant("b") })
        {
            A.CallTo(() => lifecycle.RunPendingComputedColumnBackfillsAsync(A<CancellationToken>._))
                .ReturnsLazily(async () =>
                {
                    maxParallel = Math.Max(maxParallel, Interlocked.Increment(ref running));
                    await Task.Delay(20, Ct);
                    Interlocked.Decrement(ref running);
                    return 1;
                });
        }

        await NewSut().DrainAllTenantsAsync(Ct);

        Assert.Equal(1, maxParallel);
    }

    [Fact]
    public async Task Drain_AFailingTenant_DoesNotStopTheOthers()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "bad", "good" });
        A.CallTo(() => Tenant("bad").RunPendingComputedColumnBackfillsAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("mongo down"));
        var good = Tenant("good");

        await NewSut().DrainAllTenantsAsync(Ct);

        A.CallTo(() => good.RunPendingComputedColumnBackfillsAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Drain_TenantWithoutStreamData_IsSkipped()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "nosd", "unknown" });
        var context = A.Fake<ITenantContext>();
        A.CallTo(() => context.GetArchiveLifecycleService()).Returns((IArchiveLifecycleService?)null);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("nosd")).Returns(context);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("unknown")).Returns((ITenantContext?)null);

        await NewSut().DrainAllTenantsAsync(Ct);
    }

    [Fact]
    public async Task Drain_TenantWithoutTheStreamDataModel_IsSkipped_InsteadOfThrowingEveryTick()
    {
        // test-2, 2026-11-01: tenants with stream data on but System.StreamData not imported threw
        // CkCacheException from the archive store on every tick. GetRecomputeOrchestrator is null exactly then.
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "nomodel" });
        var context = A.Fake<ITenantContext>();
        var lifecycle = A.Fake<IArchiveLifecycleService>();
        A.CallTo(() => context.GetArchiveLifecycleService()).Returns(lifecycle);
        A.CallTo(() => context.GetRecomputeOrchestrator()).Returns((IRecomputeOrchestrator?)null);
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("nomodel")).Returns(context);

        await NewSut().DrainAllTenantsAsync(Ct);

        A.CallTo(() => lifecycle.RunPendingComputedColumnBackfillsAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Service_DrainsRepeatedly_AndStopsOnShutdown()
    {
        A.CallTo(() => _tenantSource.GetTenantIdsAsync(A<CancellationToken>._)).Returns(new[] { "a" });
        var lifecycle = Tenant("a");
        var drains = 0;
        var twice = new TaskCompletionSource();
        A.CallTo(() => lifecycle.RunPendingComputedColumnBackfillsAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (Interlocked.Increment(ref drains) == 2)
                {
                    twice.TrySetResult();
                }

                return Task.FromResult(0);
            });

        var sut = NewSut();
        await sut.StartAsync(Ct);
        await twice.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await sut.StopAsync(Ct);

        Assert.True(drains >= 2);
    }
}
