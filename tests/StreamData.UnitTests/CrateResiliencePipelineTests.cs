using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// The pipeline around every CrateDB call: the timeout of a call comes from its context when it set
/// one, one circuit breaker serves every statement class, and its opening names the operation that
/// tripped it (AB#6283).
/// </summary>
public class CrateResiliencePipelineTests
{
    [Fact]
    public async Task Timeout_TakesThePerCallValueFromTheContext()
    {
        var pipeline = CrateResiliencePipeline.Build(new CrateResilienceOptions
        {
            PerAttemptTimeout = TimeSpan.FromMinutes(5),
        });
        var context = ResilienceContextPool.Shared.Get("Maintenance.non-query", CancellationToken.None);
        context.Properties.Set(CrateResiliencePipeline.CallTimeoutKey, TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            await pipeline.ExecuteAsync(
                async ctx => await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken), context));

        Assert.Equal(TimeSpan.FromMilliseconds(50), ex.Timeout);
        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task Timeout_WithoutAPerCallValue_IsTheConfiguredDefault()
    {
        var pipeline = CrateResiliencePipeline.Build(new CrateResilienceOptions
        {
            PerAttemptTimeout = TimeSpan.FromMilliseconds(50),
        });

        var ex = await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            await pipeline.ExecuteAsync(
                async token => await Task.Delay(Timeout.InfiniteTimeSpan, token), CancellationToken.None));

        Assert.Equal(TimeSpan.FromMilliseconds(50), ex.Timeout);
    }

    [Fact]
    public async Task Timeout_DoesNotEndACallThatFinishesInsideItsPerCallValue()
    {
        // The default alone would end this call; the per-call value is what lets it finish.
        var pipeline = CrateResiliencePipeline.Build(new CrateResilienceOptions
        {
            PerAttemptTimeout = TimeSpan.FromMilliseconds(20),
        });
        var context = ResilienceContextPool.Shared.Get("Maintenance.non-query", CancellationToken.None);
        context.Properties.Set(CrateResiliencePipeline.CallTimeoutKey, TimeSpan.FromMinutes(5));

        var result = await pipeline.ExecuteAsync(async ctx =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), ctx.CancellationToken);
            return 7;
        }, context);

        Assert.Equal(7, result);
        ResilienceContextPool.Shared.Return(context);
    }

    [Fact]
    public async Task CircuitBreaker_IsSharedByBothStatementClasses_AndLogsWhatTrippedIt()
    {
        var logger = new RecordingLogger();
        var pipeline = CrateResiliencePipeline.Build(
            new CrateResilienceOptions
            {
                MaxRetryAttempts = 1,
                BaseRetryDelay = TimeSpan.FromMilliseconds(1),
                CircuitMinimumThroughput = 2,
                CircuitFailureRatio = 0.5,
                CircuitSamplingDuration = TimeSpan.FromSeconds(30),
                CircuitBreakDuration = TimeSpan.FromSeconds(30),
            },
            logger);

        // Connection-class failures of maintenance statements open the breaker …
        for (var i = 0; i < 4 && logger.Entries.Count == 0; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => FailAsync(pipeline, "Maintenance.non-query"));
        }

        // … and an interactive statement is then refused by the same breaker.
        var interactive = ResilienceContextPool.Shared.Get("Interactive.non-query", CancellationToken.None);
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(_ => new ValueTask<int>(1), interactive));
        ResilienceContextPool.Shared.Return(interactive);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Maintenance.non-query", entry.Message);
        Assert.Contains(typeof(IOException).FullName!, entry.Message);
        Assert.IsType<IOException>(entry.Exception);
    }

    private static async Task FailAsync(ResiliencePipeline pipeline, string operationKey)
    {
        var context = ResilienceContextPool.Shared.Get(operationKey, CancellationToken.None);
        try
        {
            await pipeline.ExecuteAsync<int>(_ => throw new IOException("connection lost"), context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
