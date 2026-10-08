using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;

/// <summary>
///     CK v2 F1.0 (AB#5900 / AB#5901): system tenant plus recorders for tenant-update notifications, log lines and
///     the import-guard counters, so the tests can assert "no notification", "WARN" and "metric emitted".
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CkModelImportGuardFixture : SystemFixture
{
    public CkModelImportGuardFixture()
    {
        // Registered after AddRuntimeEngine's TryAdd default: the last registration wins.
        Services.AddSingleton<ITenantNotifications>(Notifications);
        Services.AddSingleton<ILoggerProvider>(Logs);
        // Test-2.0.0 (a major above Test-1.0.0) for the concurrent embedded-import test.
        Services.AddCkModelTestV2();
    }

    public RecordingTenantNotifications Notifications { get; } = new();

    public RecordingLoggerProvider Logs { get; } = new();
}

[CollectionDefinition(Name)]
public class CkModelImportGuardCollection : ICollectionFixture<CkModelImportGuardFixture>
{
    public const string Name = "CkModelImportGuard";
}

/// <summary>Counts pre/post tenant-update notifications.</summary>
public sealed class RecordingTenantNotifications : ITenantNotifications
{
    private int _preUpdates;
    private int _posUpdates;

    /// <summary>Tenant ids of every pre/post update notification, in order.</summary>
    public ConcurrentQueue<string> UpdatedTenantIds { get; } = new();

    public int PreUpdates => Volatile.Read(ref _preUpdates);

    public int PosUpdates => Volatile.Read(ref _posUpdates);

    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        Interlocked.Increment(ref _preUpdates);
        UpdatedTenantIds.Enqueue(tenantId);
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        Interlocked.Increment(ref _posUpdates);
        UpdatedTenantIds.Enqueue(tenantId);
        return Task.CompletedTask;
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId) => Task.CompletedTask;
}

/// <summary>Keeps every formatted log line with its level.</summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

    public void Dispose()
    {
    }

    /// <summary>Lines at <paramref name="level" /> that contain every one of <paramref name="fragments" />.</summary>
    public IReadOnlyList<string> Find(LogLevel level, params string[] fragments) =>
        Entries.Where(e => e.Level == level && fragments.All(f => e.Message.Contains(f, StringComparison.Ordinal)))
            .Select(e => e.Message).ToList();

    private sealed class RecordingLogger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
///     Records the measurements of the import-guard counters on meter <c>Meshmakers.Octo.MongoDb</c>.
///     Process-wide like every <see cref="MeterListener" />; tests filter by the <c>model</c> tag.
/// </summary>
public sealed class CkCounterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();

    public CkCounterRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MongoCommandObservability.MeterName &&
                instrument.Name.StartsWith("octo.ck.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagMap = new Dictionary<string, string?>();
            foreach (var tag in tags)
            {
                tagMap[tag.Key] = tag.Value?.ToString();
            }

            Measurements.Enqueue((instrument.Name, value, tagMap));
        });
        _listener.Start();
    }

    public ConcurrentQueue<(string Instrument, long Value, Dictionary<string, string?> Tags)> Measurements { get; } =
        new();

    /// <summary>Sum of the measurements of <paramref name="instrument" /> whose tags contain all <paramref name="tags" />.</summary>
    public long Sum(string instrument, params (string Key, string Value)[] tags) =>
        Measurements.Where(m => m.Instrument == instrument &&
                                tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && v == t.Value))
            .Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}
