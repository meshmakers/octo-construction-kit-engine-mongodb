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
    private readonly ConcurrentDictionary<int, PendingCommand> _pending = new();

    public MongoCommandObservability(ILogger logger, IOptionsMonitor<OctoSystemConfiguration> config)
    {
        _logger = logger;
        _config = config;
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

            LogIfSlow(e.CommandName, ctx, ms, e.RequestId);
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

    private void LogIfSlow(string commandName, PendingCommand ctx, double ms, int requestId)
    {
        var cfg = _config.CurrentValue;
        if (cfg.SlowQueryThresholdMs <= 0 || ms <= cfg.SlowQueryThresholdMs)
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
        var preview = TruncateBson(ctx.Command, cfg.SlowQueryCommandPreviewBytes);
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

    private static string TruncateBson(BsonDocument? command, int maxBytes)
    {
        if (command is null)
        {
            return "<null>";
        }

        var json = command.ToJson();
        if (json.Length <= maxBytes)
        {
            return json;
        }

        return json.Substring(0, maxBytes) + "…<truncated>";
    }

    private sealed record PendingCommand(string Database, string Target, BsonDocument? Command);
}
