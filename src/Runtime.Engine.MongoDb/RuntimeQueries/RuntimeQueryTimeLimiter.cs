using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     Applies the time limit to the MongoDB operations of one runtime-entity query (AB#8897): resolves the limit (the
///     innermost <see cref="RuntimeQueryTimeLimit" /> scope, else the configured <see cref="RuntimeQueryOptions.MaxTimeMs" />),
///     hands it to the operation as <c>maxTimeMS</c>, logs a warning when the operation completed after more than
///     <see cref="RuntimeQueryOptions.SlowQueryWarningRatio" /> of the limit, and turns MongoDB's time-out into
///     <see cref="RuntimeQueryTimeoutException" />.
/// </summary>
internal sealed class RuntimeQueryTimeLimiter
{
    private readonly TimeSpan? _configuredMaxTime;
    private readonly ILogger? _logger;

    /// <param name="tenantId">The tenant the query runs in.</param>
    /// <param name="ckTypeId">The CK type (or CK interface) the query reads.</param>
    /// <param name="configuredMaxTime">The configured limit of the host, or null when the host applies none.</param>
    /// <param name="logger">Receives the warning of a slow query; null logs nothing.</param>
    public RuntimeQueryTimeLimiter(string tenantId, string ckTypeId, TimeSpan? configuredMaxTime, ILogger? logger)
    {
        TenantId = tenantId;
        CkTypeId = ckTypeId;
        _configuredMaxTime = configuredMaxTime;
        _logger = logger;
    }

    /// <summary>The tenant the query runs in.</summary>
    public string TenantId { get; }

    /// <summary>The CK type (or CK interface) the query reads.</summary>
    public string CkTypeId { get; }

    /// <summary>
    ///     Observes the limit every operation of this limiter runs with (null: none). Null in production; set by tests
    ///     to prove which limit reached the MongoDB options.
    /// </summary>
    internal Action<string, TimeSpan?>? MaxTimeObserver { get; set; }

    /// <summary>
    ///     The limit that applies now: the innermost <see cref="RuntimeQueryTimeLimit" /> scope, else the configured one.
    /// </summary>
    public TimeSpan? ResolveMaxTime() => RuntimeQueryTimeLimit.Current is { } scope ? scope.MaxTime : _configuredMaxTime;

    /// <summary>
    ///     The aggregate options of an operation with the limit <paramref name="maxTime" />.
    /// </summary>
    public static AggregateOptions CreateAggregateOptions(TimeSpan? maxTime) => new() { MaxTime = maxTime };

    /// <summary>
    ///     The count options of an operation with the limit <paramref name="maxTime" />.
    /// </summary>
    public static CountOptions CreateCountOptions(TimeSpan? maxTime) => new() { MaxTime = maxTime };

    /// <summary>
    ///     Runs one MongoDB operation of the query under the limit that applies now.
    /// </summary>
    /// <param name="operation">What the operation does (<c>page</c>, <c>count</c>, …), for the log.</param>
    /// <param name="run">The operation; receives the limit to put into its MongoDB options.</param>
    public async Task<T> RunAsync<T>(string operation, Func<TimeSpan?, Task<T>> run)
    {
        var maxTime = ResolveMaxTime();
        MaxTimeObserver?.Invoke(operation, maxTime);

        var stopwatch = Stopwatch.StartNew();
        T result;
        try
        {
            result = await run(maxTime).ConfigureAwait(false);
        }
        catch (MongoExecutionTimeoutException ex) when (maxTime.HasValue)
        {
            stopwatch.Stop();
            throw new RuntimeQueryTimeoutException(TenantId, CkTypeId, maxTime.Value, stopwatch.Elapsed, ex);
        }

        stopwatch.Stop();
        if (maxTime.HasValue && IsSlow(stopwatch.Elapsed, maxTime.Value))
        {
            _logger?.LogWarning(
                "Slow runtime query: {Operation} on CK type {CkTypeId} in tenant {TenantId} took {DurationMs} ms, " +
                "more than {WarningPercent} % of its time limit of {MaxTimeMs} ms",
                operation, CkTypeId, TenantId, (long)stopwatch.Elapsed.TotalMilliseconds,
                (int)(RuntimeQueryOptions.SlowQueryWarningRatio * 100), (long)maxTime.Value.TotalMilliseconds);
        }

        return result;
    }

    /// <summary>
    ///     True when <paramref name="elapsed" /> is above <see cref="RuntimeQueryOptions.SlowQueryWarningRatio" /> of
    ///     <paramref name="maxTime" />.
    /// </summary>
    internal static bool IsSlow(TimeSpan elapsed, TimeSpan maxTime) =>
        elapsed.Ticks > maxTime.Ticks * RuntimeQueryOptions.SlowQueryWarningRatio;
}
