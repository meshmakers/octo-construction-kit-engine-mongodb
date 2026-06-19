using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Subscribes to MongoDB driver command events and emits OpenTelemetry histograms / counters
/// plus structured slow-query log lines. Lives at the I/O boundary so application code paths
/// stay unchanged. Replacement for the Atlas / Enterprise Performance Advisor on Community Edition.
/// </summary>
internal sealed class MongoCommandObservability
{
    /// <summary>Meter name registered in <c>ObservabilityBuilder</c>.</summary>
    public const string MeterName = "Meshmakers.Octo.MongoDb";

    /// <summary>Cap on the in-flight request map. On overflow the map is cleared.</summary>
    internal const int MaxPendingCommands = 10_000;

    /// <summary>
    /// Commands suppressed from logging and metrics. Driver heartbeats / handshakes fire at
    /// connection-pool frequency and would dominate the histogram cardinality budget without
    /// carrying useful application signal.
    /// </summary>
    private static readonly HashSet<string> IgnoredCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "isMaster", "hello", "ping", "buildInfo",
        "saslStart", "saslContinue", "saslContinueOrFinish",
        "endSessions", "getMore"
    };

    /// <summary>
    /// Per-request accumulator scope. The AsyncLocal value flows through ExecutionContext into
    /// the MongoDB driver's command-event callbacks, so commands issued during the scope are
    /// summed into the active <see cref="RequestMongoStats"/> instance. Out-of-scope work
    /// (background jobs, Mesh-Adapter pipelines) sees <c>null</c> and is left untouched —
    /// only the metrics and slow-log paths fire there. Verified by AsyncLocalDriverFlowSpike.
    /// </summary>
    private static readonly AsyncLocal<RequestMongoStats?> CurrentScope = new();

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "octo.mongodb.command.duration",
        unit: "ms",
        description: "MongoDB command execution duration");

    private static readonly Counter<long> CommandErrors = Meter.CreateCounter<long>(
        "octo.mongodb.command.errors",
        unit: "count",
        description: "MongoDB command failures, tagged by error code");

    private readonly ILogger _logger;
    private readonly IOptionsMonitor<OctoSystemConfiguration> _config;
    private readonly SlowQueriesBuffer? _slowQueries;
    private readonly ConcurrentDictionary<int, PendingCommand> _pending = new();

    public MongoCommandObservability(
        ILogger logger,
        IOptionsMonitor<OctoSystemConfiguration> config,
        SlowQueriesBuffer? slowQueries = null)
    {
        _logger = logger;
        _config = config;
        _slowQueries = slowQueries;
    }

    /// <summary>
    /// Opens a per-request scope so that subsequent MongoDB commands on this async-flow are
    /// summed into <paramref name="stats"/>. Dispose the returned handle to close the scope —
    /// typically from an HTTP middleware's <c>OnStarting</c> callback or a GraphQL document
    /// execution listener.
    /// </summary>
    /// <remarks>
    /// Nesting: opening a scope while another is active replaces it for the duration; disposing
    /// restores the previous one. This is intentional — production code should not nest, but
    /// tests sometimes do, and silent corruption would be worse than restore-on-dispose.
    /// </remarks>
    public static IDisposable BeginRequestScope(out RequestMongoStats stats)
    {
        stats = new RequestMongoStats();
        var previous = CurrentScope.Value;
        CurrentScope.Value = stats;
        return new ScopeReset(previous);
    }

    /// <summary>
    /// Returns the currently active <see cref="RequestMongoStats"/> for this async flow, or
    /// <c>null</c> if no scope is open. Surfaced publicly via <see cref="MongoRequestScope.Current"/>;
    /// production callers should use that façade rather than this internal accessor.
    /// </summary>
    internal static RequestMongoStats? GetCurrentScope() => CurrentScope.Value;

    private sealed class ScopeReset(RequestMongoStats? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentScope.Value = previous;
            }
        }
    }

    /// <summary>
    /// Captures correlation data from <see cref="CommandStartedEvent"/>. Required because
    /// the database name and the command body are not carried in succeeded / failed events.
    /// </summary>
    public void OnStarted(CommandStartedEvent e)
    {
        if (IgnoredCommands.Contains(e.CommandName))
        {
            return;
        }

        try
        {
            // Bounded growth: if the map is full (commands lost their finish event, e.g. dropped
            // connection), drop the oldest by clearing. Pragmatic — accuracy of slow-query stats
            // matters more than perfect correlation in degenerate cases.
            if (_pending.Count >= MaxPendingCommands)
            {
                _pending.Clear();
            }

            _pending[e.RequestId] = new PendingCommand(
                Database: e.DatabaseNamespace?.DatabaseName ?? "unknown",
                Target: ExtractCommandTarget(e.Command),
                Command: e.Command);
        }
        catch (Exception ex)
        {
            SafeLogError(ex, "MongoCommandObservability.OnStarted threw");
        }
    }

    public void OnSucceeded(CommandSucceededEvent e)
    {
        if (IgnoredCommands.Contains(e.CommandName))
        {
            return;
        }

        try
        {
            var ctx = _pending.TryRemove(e.RequestId, out var p)
                ? p
                : new PendingCommand("unknown", "unknown", null);

            var ms = e.Duration.TotalMilliseconds;

            CommandDuration.Record(ms,
                new KeyValuePair<string, object?>("command_name", e.CommandName),
                new KeyValuePair<string, object?>("database", ctx.Database),
                new KeyValuePair<string, object?>("status", "success"));

            CurrentScope.Value?.Record(e.CommandName, ms);

            HandleSlow(e.CommandName, ctx, ms, e.RequestId, success: true, errorCode: null);
        }
        catch (Exception ex)
        {
            SafeLogError(ex, "MongoCommandObservability.OnSucceeded threw");
        }
    }

    public void OnFailed(CommandFailedEvent e)
    {
        if (IgnoredCommands.Contains(e.CommandName))
        {
            return;
        }

        try
        {
            var ctx = _pending.TryRemove(e.RequestId, out var p)
                ? p
                : new PendingCommand("unknown", "unknown", null);

            var ms = e.Duration.TotalMilliseconds;
            var errorCode = e.Failure is MongoCommandException mce
                ? mce.Code.ToString()
                : "unknown";

            CommandDuration.Record(ms,
                new KeyValuePair<string, object?>("command_name", e.CommandName),
                new KeyValuePair<string, object?>("database", ctx.Database),
                new KeyValuePair<string, object?>("status", "failure"));

            CommandErrors.Add(1,
                new KeyValuePair<string, object?>("command_name", e.CommandName),
                new KeyValuePair<string, object?>("database", ctx.Database),
                new KeyValuePair<string, object?>("error_code", errorCode));

            CurrentScope.Value?.Record(e.CommandName, ms);

            HandleSlow(e.CommandName, ctx, ms, e.RequestId, success: false, errorCode: errorCode);

            _logger.LogWarning(e.Failure,
                "MongoDB command failed: {CommandName} {Target} on {Database} after {DurationMs}ms (errorCode={ErrorCode}, requestId={RequestId})",
                e.CommandName, ctx.Target, ctx.Database, ms, errorCode, e.RequestId);
        }
        catch (Exception ex)
        {
            SafeLogError(ex, "MongoCommandObservability.OnFailed threw");
        }
    }

    // Driver command events fire on the driver's thread pool; an exception here must never
    // propagate into the driver. Even the fallback logger can throw (test doubles, broken sinks),
    // so the LogError call itself must be guarded.
    private void SafeLogError(Exception ex, string message)
    {
        try
        {
            _logger.LogError(ex, "{Message}", message);
        }
        catch
        {
            // intentional swallow — we cannot afford to throw back into the driver
        }
    }

    private void HandleSlow(
        string commandName, PendingCommand ctx, double ms, int requestId,
        bool success, string? errorCode)
    {
        var cfg = _config.CurrentValue;
        if (cfg.SlowQueryThresholdMs <= 0 || ms <= cfg.SlowQueryThresholdMs)
        {
            return;
        }

        // Truncated BSON preview is needed in two places (buffer entry + optionally the WARN
        // log when above SlowQueryFullCommandLogMs). Compute once, share between both.
        var preview = TruncateBson(ctx.Command, cfg.SlowQueryCommandPreviewBytes);

        // Studio diagnostics surface: always capture every slow command (success or failure)
        // so a tenant admin can inspect what was slow. Tenant filtering happens at the
        // read endpoint, keyed off the entry's Database field.
        _slowQueries?.Add(new SlowQueryEntry(
            Timestamp: DateTimeOffset.UtcNow,
            CommandName: commandName,
            Target: ctx.Target,
            Database: ctx.Database,
            DurationMs: ms,
            RequestId: requestId,
            CommandBsonPreview: preview,
            Success: success,
            ErrorCode: errorCode));

        // Slow-log: only for successful commands. Failures are already logged at WARN in
        // OnFailed with the exception attached; a second slow-log here would just duplicate.
        if (!success)
        {
            return;
        }

        // Short slow-log: identifies the operation + target (e.g. aggregate on rt_entities).
        // Sufficient to find the query without dumping the whole pipeline body.
        if (cfg.SlowQueryFullCommandLogMs <= 0 || ms <= cfg.SlowQueryFullCommandLogMs)
        {
            _logger.LogWarning(
                "Slow MongoDB command: {CommandName} {Target} on {Database} took {DurationMs}ms (requestId={RequestId})",
                commandName, ctx.Target, ctx.Database, ms, requestId);
            return;
        }

        // Very slow → include truncated BSON body so the exact filter / pipeline is captured.
        _logger.LogWarning(
            "Slow MongoDB command: {CommandName} {Target} on {Database} took {DurationMs}ms (requestId={RequestId}) command={Command}",
            commandName, ctx.Target, ctx.Database, ms, requestId, preview);
    }

    /// <summary>
    /// In a MongoDB command BSON, the first element's value is conventionally the operation
    /// target — e.g. <c>{ aggregate: "rt_entities", pipeline: [...] }</c> or
    /// <c>{ find: "ck_types", filter: {...} }</c>. We use it to make slow-log lines actionable.
    /// </summary>
    private static string ExtractCommandTarget(BsonDocument? command)
    {
        if (command is null || command.ElementCount == 0)
        {
            return "unknown";
        }

        var first = command.GetElement(0);
        return first.Value switch
        {
            BsonString s => s.Value,
            BsonInt32 i => i.Value.ToString(),
            BsonInt64 l => l.Value.ToString(),
            _ => first.Value?.ToString() ?? "unknown"
        };
    }

    /// <summary>
    /// Truncates the BSON-as-JSON representation so it fits within <paramref name="maxBytes"/>
    /// bytes when UTF-8 encoded — i.e. the same byte budget the log sink will spend. Naïve
    /// char-based truncation under-counts non-ASCII content (each German umlaut is 2 UTF-8 bytes,
    /// emoji are 4) and can also split a UTF-16 surrogate pair, producing an invalid string.
    /// </summary>
    internal static string TruncateBson(BsonDocument? command, int maxBytes)
    {
        if (command is null)
        {
            return "<null>";
        }

        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var json = command.ToJson();
        if (System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes)
        {
            return json;
        }

        // Walk runes, accumulating their UTF-8 byte length. Stops on the last whole rune
        // that fits inside the budget — never splits a multi-byte sequence or a surrogate pair.
        var span = json.AsSpan();
        var accumulatedBytes = 0;
        var charsConsumed = 0;
        foreach (var rune in span.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (accumulatedBytes + runeBytes > maxBytes)
            {
                break;
            }

            accumulatedBytes += runeBytes;
            charsConsumed += rune.Utf16SequenceLength;
        }

        return json.Substring(0, charsConsumed) + "…<truncated>";
    }

    private sealed record PendingCommand(string Database, string Target, BsonDocument? Command);
}
