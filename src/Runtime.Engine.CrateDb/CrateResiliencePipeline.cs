using System.IO;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Builds the Polly resilience pipeline that wraps every CrateDB call from
/// <c>CrateDatabaseClient</c>. Concept §8 T13 — three layers, in outside-in order:
/// timeout (one budget across the call and its retries), retry (handles transient
/// PostgreSQL/Npgsql errors with exponential backoff + jitter), circuit breaker (cuts the cluster
/// off after sustained failure so callers fail fast and metrics can flag the outage).
/// </summary>
/// <remarks>
/// One pipeline, and therefore one circuit breaker, serves every statement class. A call that
/// needs another budget than <see cref="CrateResilienceOptions.PerAttemptTimeout"/> sets it on its
/// <see cref="ResilienceContext"/> (<see cref="CallTimeoutKey"/>); the timeout only ends a call
/// whose callback observes the context's cancellation token.
/// </remarks>
public static class CrateResiliencePipeline
{
    /// <summary>
    /// Default per-operation timeout. CrateDB's planner can take a few seconds on cold queries;
    /// 30 s is generous for OLTP-style inserts and most analytic queries while still bounded.
    /// </summary>
    public static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Time the pipeline's timeout is set behind a call's command timeout, so that the driver's own
    /// timeout fires first and yields the precise error.
    /// </summary>
    internal static readonly TimeSpan CallTimeoutMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Context property carrying the timeout of one call. Absent (or not positive) ⇒
    /// <see cref="CrateResilienceOptions.PerAttemptTimeout"/>.
    /// </summary>
    internal static readonly ResiliencePropertyKey<TimeSpan> CallTimeoutKey = new("crate.call-timeout");

    /// <summary>
    /// Builds the default resilience pipeline. Public so consumers can compose extra strategies
    /// or replace it for tests.
    /// </summary>
    /// <param name="options">Tunables; defaults when null.</param>
    /// <param name="logger">
    /// When given, the opening of the circuit breaker is logged with the operation that tripped it.
    /// </param>
    public static ResiliencePipeline Build(CrateResilienceOptions? options = null, ILogger? logger = null)
    {
        options ??= new CrateResilienceOptions();

        var builder = new ResiliencePipelineBuilder();

        // Outermost: the timeout. It wraps the retry and the underlying call, so it is one budget
        // across the first attempt and its retries, not a budget per attempt. The value comes from
        // the call's context when the call set one.
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = options.PerAttemptTimeout,
            TimeoutGenerator = args => new ValueTask<TimeSpan>(
                args.Context.Properties.TryGetValue(CallTimeoutKey, out var callTimeout) && callTimeout > TimeSpan.Zero
                    ? callTimeout
                    : options.PerAttemptTimeout),
            Name = "crate.timeout",
        });

        // Middle: retry on transient failures with exponential backoff + jitter. NpgsqlException
        // covers connection/socket/timeout errors; we exclude PostgresException which already
        // signals the server rejected the SQL (won't get better on retry). We ALSO handle the raw
        // stream/IO/socket class (IOException incl. EndOfStreamException "Attempted to read past the
        // end of the stream", SocketException) because a connection dropped mid-read surfaces either
        // wrapped in NpgsqlException ("Exception while reading from stream") OR as the bare
        // IOException/EndOfStreamException — both are transient and must be retried (AB#4278).
        builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<NpgsqlException>(ex => ex is not PostgresException)
                .Handle<TimeoutRejectedException>()
                .Handle<IOException>()
                .Handle<SocketException>(),
            MaxRetryAttempts = options.MaxRetryAttempts,
            Delay = options.BaseRetryDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Name = "crate.retry",
        });

        // Innermost: circuit breaker. Caps the failure burst from a downed cluster so callers
        // fail fast (BrokenCircuitException) and observability can flag the outage immediately.
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<NpgsqlException>(ex => ex is not PostgresException)
                .Handle<TimeoutRejectedException>()
                .Handle<IOException>()
                .Handle<SocketException>(),
            FailureRatio = options.CircuitFailureRatio,
            SamplingDuration = options.CircuitSamplingDuration,
            MinimumThroughput = options.CircuitMinimumThroughput,
            BreakDuration = options.CircuitBreakDuration,
            Name = "crate.circuit-breaker",
            // Which call site fed the breaker decides whether an outage was caused by background
            // statements or hit interactive ones as well; without this line that cannot be told.
            OnOpened = args =>
            {
                logger?.LogWarning(
                    args.Outcome.Exception,
                    "CrateDB circuit breaker opened for {BreakDuration}; tripped by operation '{OperationKey}' with {ExceptionType}.",
                    args.BreakDuration,
                    args.Context.OperationKey ?? "(none)",
                    args.Outcome.Exception?.GetType().FullName ?? "(no exception)");
                return default;
            },
        });

        return builder.Build();
    }
}

/// <summary>
/// Tunables for <see cref="CrateResiliencePipeline.Build"/>. Defaults match the values picked in
/// concept §8 T13 — they balance fail-fast under outage with tolerating brief CrateDB master
/// election windows during rolling restarts.
/// </summary>
public sealed class CrateResilienceOptions
{
    /// <summary>
    /// Timeout of one call including its retries (default 30 s). The name is historical: the
    /// timeout is the outermost layer of the pipeline, so the retries share it.
    /// </summary>
    public TimeSpan PerAttemptTimeout { get; set; } = CrateResiliencePipeline.DefaultPerAttemptTimeout;

    /// <summary>Maximum retry attempts after the initial call (default 3).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base retry delay (default 200 ms; exponential + jitter).</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Failure-ratio threshold to open the breaker (default 0.5).</summary>
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>Sliding window the failure ratio is computed over (default 30 s).</summary>
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum number of calls in the sampling window before the breaker can open (default 10).</summary>
    public int CircuitMinimumThroughput { get; set; } = 10;

    /// <summary>How long the breaker stays open before transitioning to half-open (default 30 s).</summary>
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
