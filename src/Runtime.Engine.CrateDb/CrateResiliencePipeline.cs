using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Builds the Polly resilience pipeline that wraps every CrateDB call from
/// <c>CrateDatabaseClient</c>. Concept §8 T13 — three layers, in outside-in order:
/// timeout (each attempt is bounded), retry (handles transient PostgreSQL/Npgsql errors with
/// exponential backoff + jitter), circuit breaker (cuts the cluster off after sustained failure
/// so callers fail fast and metrics can flag the outage).
/// </summary>
public static class CrateResiliencePipeline
{
    /// <summary>
    /// Default per-operation timeout. CrateDB's planner can take a few seconds on cold queries;
    /// 30 s is generous for OLTP-style inserts and most analytic queries while still bounded.
    /// </summary>
    public static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds the default resilience pipeline. Public so consumers can compose extra strategies
    /// or replace it for tests.
    /// </summary>
    public static ResiliencePipeline Build(CrateResilienceOptions? options = null)
    {
        options ??= new CrateResilienceOptions();

        var builder = new ResiliencePipelineBuilder();

        // Outermost: per-attempt timeout. Wraps the retry + the underlying call so each retry
        // gets its own clean budget rather than competing with prior attempts.
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = options.PerAttemptTimeout,
            Name = "crate.timeout",
        });

        // Middle: retry on transient failures with exponential backoff + jitter. NpgsqlException
        // covers connection/socket/timeout errors; we exclude PostgresException which already
        // signals the server rejected the SQL (won't get better on retry).
        builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<NpgsqlException>(ex => ex is not PostgresException)
                .Handle<TimeoutRejectedException>(),
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
                .Handle<TimeoutRejectedException>(),
            FailureRatio = options.CircuitFailureRatio,
            SamplingDuration = options.CircuitSamplingDuration,
            MinimumThroughput = options.CircuitMinimumThroughput,
            BreakDuration = options.CircuitBreakDuration,
            Name = "crate.circuit-breaker",
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
    /// <summary>Per-attempt timeout (default 30 s).</summary>
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
