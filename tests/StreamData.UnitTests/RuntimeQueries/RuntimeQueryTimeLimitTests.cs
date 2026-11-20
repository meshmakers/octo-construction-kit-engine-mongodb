using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.RuntimeQueries;

/// <summary>
///     AB#8897: the time limit of runtime queries - the call-site override scope, the resolution of the limit, the
///     MongoDB options it ends up in, the coded time-out and the 80 % warning.
/// </summary>
public class RuntimeQueryTimeLimitTests
{
    private static readonly TimeSpan Configured = TimeSpan.FromSeconds(120);

    [Fact]
    public void Override_AppliesInsideTheScope_AndFallsBackAfterDispose()
    {
        Assert.Null(RuntimeQueryTimeLimit.Current);

        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(30)))
        {
            Assert.Equal(TimeSpan.FromMinutes(30), RuntimeQueryTimeLimit.Current!.MaxTime);
        }

        Assert.Null(RuntimeQueryTimeLimit.Current);
    }

    [Fact]
    public void NestedScopes_InnermostWins_AndDisposeRestoresTheOuter()
    {
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(30)))
        {
            using (RuntimeQueryTimeLimit.Disable())
            {
                Assert.Null(RuntimeQueryTimeLimit.Current!.MaxTime);
            }

            Assert.Equal(TimeSpan.FromMinutes(30), RuntimeQueryTimeLimit.Current!.MaxTime);
        }

        Assert.Null(RuntimeQueryTimeLimit.Current);
    }

    [Fact]
    public void DisposingTwice_DoesNotDropAnOuterScope()
    {
        using var outer = RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(5));
        var inner = RuntimeQueryTimeLimit.Disable();
        inner.Dispose();
        inner.Dispose();

        Assert.Equal(TimeSpan.FromMinutes(5), RuntimeQueryTimeLimit.Current!.MaxTime);
    }

    [Fact]
    public async Task Scope_HoldsAcrossAwait()
    {
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(10)))
        {
            await Task.Yield();
            await Task.Delay(5, TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(10), RuntimeQueryTimeLimit.Current!.MaxTime);
        }

        Assert.Null(RuntimeQueryTimeLimit.Current);
    }

    [Fact]
    public async Task TasksStartedInsideTheScope_InheritIt_EvenWhenTheyFinishAfterDispose()
    {
        var release = new TaskCompletionSource();
        Task<TimeSpan?> started;
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(10)))
        {
            started = Task.Run(async () =>
            {
                await release.Task;
                return RuntimeQueryTimeLimit.Current?.MaxTime;
            });
        }

        release.SetResult();
        Assert.Equal(TimeSpan.FromMinutes(10), await started);
        Assert.Null(RuntimeQueryTimeLimit.Current);
    }

    [Fact]
    public async Task ParallelTasks_KeepTheirOwnScopes_AndNoScopeLeaksToTheCaller()
    {
        using var barrier = new Barrier(3);

        async Task<TimeSpan?> RunWith(Func<IDisposable>? openScope)
        {
            await Task.Yield();
            using var scope = openScope?.Invoke();
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            await Task.Delay(10, TestContext.Current.CancellationToken);
            return RuntimeQueryTimeLimit.Current?.MaxTime;
        }

        var results = await Task.WhenAll(
            Task.Run(() => RunWith(() => RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(1)))),
            Task.Run(() => RunWith(RuntimeQueryTimeLimit.Disable)),
            Task.Run(() => RunWith(null)));

        Assert.Equal(TimeSpan.FromMinutes(1), results[0]);
        Assert.Null(results[1]);
        Assert.Null(results[2]);
        Assert.Null(RuntimeQueryTimeLimit.Current);
    }

    [Fact]
    public void Override_RefusesAZeroOrNegativeLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeQueryTimeLimit.Override(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeQueryTimeLimit.Override(TimeSpan.FromSeconds(-1)));
        Assert.Null(RuntimeQueryTimeLimit.Current);
    }

    [Theory]
    [InlineData(120_000, 120_000)]
    [InlineData(5_000, 5_000)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void Options_MaxTime_IsOffForZeroOrNegative(int maxTimeMs, int? expectedMs)
    {
        var options = new RuntimeQueryOptions { MaxTimeMs = maxTimeMs };

        Assert.Equal(expectedMs.HasValue ? TimeSpan.FromMilliseconds(expectedMs.Value) : null, options.MaxTime);
    }

    [Fact]
    public void Options_DefaultIs120Seconds()
    {
        Assert.Equal(120_000, new RuntimeQueryOptions().MaxTimeMs);
        Assert.Equal("RuntimeQuery", RuntimeQueryOptions.SectionName);
    }

    [Fact]
    public void ResolveMaxTime_IsTheConfiguredLimit_UnlessAScopeOverridesIt()
    {
        var limiter = new RuntimeQueryTimeLimiter("t1", "Test/Type-1", Configured, null);

        Assert.Equal(Configured, limiter.ResolveMaxTime());
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(30)))
        {
            Assert.Equal(TimeSpan.FromMinutes(30), limiter.ResolveMaxTime());
        }

        using (RuntimeQueryTimeLimit.Disable())
        {
            Assert.Null(limiter.ResolveMaxTime());
        }

        Assert.Equal(Configured, limiter.ResolveMaxTime());
    }

    [Fact]
    public void ResolveMaxTime_WithoutConfiguredLimit_TakesOnlyAnExplicitOverride()
    {
        var limiter = new RuntimeQueryTimeLimiter("t1", "Test/Type-1", null, null);

        Assert.Null(limiter.ResolveMaxTime());
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(TimeSpan.FromSeconds(10), limiter.ResolveMaxTime());
        }
    }

    [Fact]
    public void TheLimit_ReachesAggregateOptionsAndCountOptions()
    {
        Assert.Equal(Configured, RuntimeQueryTimeLimiter.CreateAggregateOptions(Configured).MaxTime);
        Assert.Equal(Configured, RuntimeQueryTimeLimiter.CreateCountOptions(Configured).MaxTime);
        Assert.Null(RuntimeQueryTimeLimiter.CreateAggregateOptions(null).MaxTime);
        Assert.Null(RuntimeQueryTimeLimiter.CreateCountOptions(null).MaxTime);
    }

    [Fact]
    public async Task RunAsync_HandsTheOverrideToTheOperation_InsteadOfTheDefault()
    {
        var limiter = new RuntimeQueryTimeLimiter("t1", "Test/Type-1", Configured, null);
        var observed = new List<TimeSpan?>();

        await limiter.RunAsync("page", maxTime =>
        {
            observed.Add(RuntimeQueryTimeLimiter.CreateAggregateOptions(maxTime).MaxTime);
            return Task.FromResult(0);
        });
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromHours(1)))
        {
            await limiter.RunAsync("count", maxTime =>
            {
                observed.Add(RuntimeQueryTimeLimiter.CreateCountOptions(maxTime).MaxTime);
                return Task.FromResult(0);
            });
        }

        using (RuntimeQueryTimeLimit.Disable())
        {
            await limiter.RunAsync("page", maxTime =>
            {
                observed.Add(RuntimeQueryTimeLimiter.CreateAggregateOptions(maxTime).MaxTime);
                return Task.FromResult(0);
            });
        }

        Assert.Equal([Configured, TimeSpan.FromHours(1), null], observed);
    }

    [Fact]
    public async Task RunAsync_TurnsMongoDbsTimeOutIntoTheCodedException()
    {
        var limiter = new RuntimeQueryTimeLimiter("tenant-a", "Test/Machine-1", TimeSpan.FromMilliseconds(250), null);

        var exception = await Assert.ThrowsAsync<RuntimeQueryTimeoutException>(() =>
            limiter.RunAsync<int>("count", _ => throw CreateMongoTimeout()));

        Assert.Equal("tenant-a", exception.TenantId);
        Assert.Equal("Test/Machine-1", exception.CkTypeId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), exception.MaxTime);
        Assert.IsType<MongoExecutionTimeoutException>(exception.InnerException);
        Assert.Contains("'Test/Machine-1'", exception.Message);
        Assert.Contains("250 ms", exception.Message);
    }

    [Fact]
    public async Task RunAsync_WithoutALimit_LetsOtherTimeOutsThrough()
    {
        var limiter = new RuntimeQueryTimeLimiter("tenant-a", "Test/Machine-1", null, null);

        await Assert.ThrowsAsync<MongoExecutionTimeoutException>(() =>
            limiter.RunAsync<int>("count", _ => throw CreateMongoTimeout()));
    }

    [Fact]
    public async Task RunAsync_LogsAWarning_WhenTheOperationUsedMoreThan80PercentOfTheLimit()
    {
        var logger = new CapturingLogger();
        var limiter = new RuntimeQueryTimeLimiter("tenant-a", "Test/Machine-1", TimeSpan.FromMilliseconds(50), logger);

        await limiter.RunAsync("page", async _ =>
        {
            await Task.Delay(80, TestContext.Current.CancellationToken);
            return 0;
        });

        var line = Assert.Single(logger.Lines);
        Assert.StartsWith("Warning:", line);
        Assert.Contains("Test/Machine-1", line);
        Assert.Contains("tenant-a", line);
        Assert.Contains("time limit of 50 ms", line);
    }

    [Fact]
    public async Task RunAsync_LogsNothing_ForAFastOperationOrWithoutALimit()
    {
        var logger = new CapturingLogger();
        var limited = new RuntimeQueryTimeLimiter("tenant-a", "Test/Machine-1", TimeSpan.FromMinutes(2), logger);
        var unlimited = new RuntimeQueryTimeLimiter("tenant-a", "Test/Machine-1", null, logger);

        await limited.RunAsync("page", _ => Task.FromResult(0));
        await unlimited.RunAsync("page", async _ =>
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Empty(logger.Lines);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(81, true)]
    [InlineData(100, true)]
    [InlineData(10, false)]
    public void IsSlow_MeansMoreThan80Percent(int elapsedSeconds, bool expected)
    {
        Assert.Equal(expected,
            RuntimeQueryTimeLimiter.IsSlow(TimeSpan.FromSeconds(elapsedSeconds), TimeSpan.FromSeconds(100)));
    }

    private static MongoExecutionTimeoutException CreateMongoTimeout()
    {
        var serverId = new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017));
        return new MongoExecutionTimeoutException(new ConnectionId(serverId), "operation exceeded time limit");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
