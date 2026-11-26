using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.RuntimeQueries;

/// <summary>
///     The runtime read guard: what counts as unbounded, one warning and one count per execution and node, reads
///     without execution rate-limited, suppressed reads and mode Off silent, and a counter whose first hit is
///     countable (exported as 0 first).
/// </summary>
public sealed class RuntimeReadGuardTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string CkType = "Test-1.0.0/Meter";

    private readonly CapturingLogger _logger = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RuntimeReadGuardOptions _options = new();
    private readonly RuntimeReadGuard _guard;

    public RuntimeReadGuardTests()
    {
        _guard = new RuntimeReadGuard(() => _options, new RuntimeReadGuardMetrics(_meterFactory), _logger, _time);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(int.MaxValue, null, true)]
    [InlineData(1001, null, true)]
    [InlineData(1000, null, false)]
    [InlineData(0, null, false)]
    [InlineData(null, 1000, false)]
    [InlineData(null, 1001, true)]
    [InlineData(int.MaxValue, 5, false)]
    [InlineData(-1, null, true)]
    public void IsUnbounded_TakeAboveLimitOrMissing_UnlessKeysBoundIt(int? take, int? keyCount, bool expected)
    {
        Assert.Equal(expected, RuntimeReadGuard.IsUnbounded(take, keyCount, 1000));
    }

    [Fact]
    public void Observe_InsideNode_WarnsOncePerExecutionAndNode_AndCountsOnce()
    {
        using var execution = RuntimeReadScope.BeginExecution(
            new RuntimeReadExecution(Tenant, "pipeline-1", null, "flow-1", "exec-1"));
        using (RuntimeReadScope.BeginNode("GetRtEntitiesByType@1", "PipelineExecution/ForEach@1/GetRtEntitiesByType@1",
                   "0:PipelineExecution/1:ForEach@1/0:GetRtEntitiesByType@1"))
        {
            for (var i = 0; i < 5; i++)
            {
                _guard.Observe(Tenant, "ByType", CkType, null, null, 1234);
            }
        }

        var warning = Assert.Single(_logger.Warnings);
        Assert.Equal(RuntimeReadGuardEvents.UnboundedRead, warning.EventId);
        Assert.Equal("GetRtEntitiesByType@1", warning.State["NodeType"]);
        Assert.Equal("PipelineExecution/ForEach@1/GetRtEntitiesByType@1", warning.State["NodePath"]);
        Assert.Equal("pipeline-1", warning.State["PipelineId"]);
        Assert.Equal("flow-1", warning.State["DataFlowId"]);
        Assert.Equal("exec-1", warning.State["ExecutionId"]);
        Assert.Equal(CkType, warning.State["CkTypeId"]);
        Assert.Equal(Tenant, warning.State["TenantId"]);
        Assert.Equal("null", warning.State["Take"]);
        Assert.Equal(1234L, warning.State["ReturnedCount"]);

        Assert.Equal(1, CollectCount(Tenant, CkType, "GetRtEntitiesByType@1"));
    }

    [Fact]
    public void Observe_TwoNodesAndTwoExecutions_EachReportedOnce()
    {
        foreach (var executionId in new[] { "exec-1", "exec-2" })
        {
            using var execution = RuntimeReadScope.BeginExecution(
                new RuntimeReadExecution(Tenant, "pipeline-1", null, "flow-1", executionId));
            foreach (var key in new[] { "0:A", "1:A" })
            {
                using var node = RuntimeReadScope.BeginNode("A@1", "PipelineExecution/A@1", key);
                _guard.Observe(Tenant, "ByType", CkType, int.MaxValue, null, 3);
                _guard.Observe(Tenant, "ByType", CkType, int.MaxValue, null, 3);
            }
        }

        Assert.Equal(4, _logger.Warnings.Count);
        Assert.All(_logger.Warnings, w => Assert.Equal("int.MaxValue", w.State["Take"]));
        Assert.Equal(4, CollectCount(Tenant, CkType, "A@1"));
    }

    [Fact]
    public void Observe_BoundedRead_LogsAndCountsNothing()
    {
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using var node = RuntimeReadScope.BeginNode("A@1", "A@1");

        _guard.Observe(Tenant, "ByType", CkType, 1000, null, 1000);
        _guard.Observe(Tenant, "ById", CkType, null, 20, 20);

        Assert.Empty(_logger.Warnings);
        Assert.Equal(0, CollectCount(Tenant, CkType, "A@1"));
    }

    [Fact]
    public void Observe_ModeOff_LogsAndCountsNothing()
    {
        _options.Mode = "off";
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using var node = RuntimeReadScope.BeginNode("A@1", "A@1");

        _guard.Observe(Tenant, "ByType", CkType, null, null, 5);

        Assert.Empty(_logger.Warnings);
        Assert.Equal(0, CollectCount(Tenant, CkType, "A@1"));
    }

    [Fact]
    public void Defaults_AreWarnAnd1000_AndNoModeBeyondOffAndWarnExists()
    {
        // AB#9824 is visibility only: the default warns, and no mode that caps or refuses a read exists (AB#8911).
        var options = new RuntimeReadGuardOptions();
        Assert.Equal("Warn", options.Mode);
        Assert.True(options.TryGetMode(out var mode));
        Assert.Equal(RuntimeReadGuardMode.Warn, mode);
        Assert.Equal(1000, options.MaxTake);
        Assert.Equal(1000, RuntimeReadGuardOptions.DefaultMaxTake);
        Assert.Equal(["Off", "Warn"], Enum.GetNames<RuntimeReadGuardMode>());
    }

    [Theory]
    [InlineData("Enforce")]
    [InlineData("Refuse")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("Off,Warn")]
    public void InvalidMode_IsLoggedOnce_AndTheGuardRunsAsWarn(string invalid)
    {
        _options.Mode = invalid;
        var guard = new RuntimeReadGuard(() => _options, new RuntimeReadGuardMetrics(_meterFactory), _logger, _time);
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using (RuntimeReadScope.BeginNode("A@1", "A@1", "0:A@1"))
        {
            guard.Observe(Tenant, "ByType", CkType, null, null, 5);
        }

        using (RuntimeReadScope.BeginNode("B@1", "B@1", "1:B@1"))
        {
            guard.Observe(Tenant, "ByType", CkType, null, null, 5);
        }

        var configWarning = Assert.Single(_logger.Warnings,
            w => w.EventId == RuntimeReadGuardEvents.InvalidConfiguration);
        Assert.Equal(invalid, configWarning.State["InvalidMode"]);
        Assert.Equal("Off | Warn", configWarning.State["ValidModes"]);
        Assert.Equal(2, _logger.Warnings.Count(w => w.EventId == RuntimeReadGuardEvents.UnboundedRead));
        Assert.True(guard.IsCandidate(null, null));
    }

    [Fact]
    public void UnbindableConfiguration_IsLoggedOnce_AndTheGuardRunsAsWarnWithTheDefaultLimit()
    {
        var guard = new RuntimeReadGuard(() => throw new InvalidOperationException("MaxTake 'abc' is no integer"),
            new RuntimeReadGuardMetrics(_meterFactory), _logger, _time);
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using var node = RuntimeReadScope.BeginNode("A@1", "A@1");

        guard.Observe(Tenant, "ByType", CkType, 1000, null, 1000);
        guard.Observe(Tenant, "ByType", CkType, 1001, null, 1001);

        Assert.Single(_logger.Warnings, w => w.EventId == RuntimeReadGuardEvents.InvalidConfiguration);
        Assert.Equal(1000, Assert.Single(_logger.Warnings,
            w => w.EventId == RuntimeReadGuardEvents.UnboundedRead).State["MaxTake"]);
    }

    [Theory]
    [InlineData("Off", RuntimeReadGuardMode.Off)]
    [InlineData(" warn ", RuntimeReadGuardMode.Warn)]
    [InlineData("OFF", RuntimeReadGuardMode.Off)]
    public void ValidMode_IsParsedCaseInsensitive_WithoutConfigurationWarning(string text,
        RuntimeReadGuardMode expected)
    {
        _options.Mode = text;
        var guard = new RuntimeReadGuard(() => _options, new RuntimeReadGuardMetrics(_meterFactory), _logger, _time);

        Assert.Equal(expected, guard.ResolveSettings().Mode);
        Assert.DoesNotContain(_logger.Warnings, w => w.EventId == RuntimeReadGuardEvents.InvalidConfiguration);
    }

    [Fact]
    public void Observe_MaxTakeFromOptions_IsTheThreshold()
    {
        _options.MaxTake = 10;
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using var node = RuntimeReadScope.BeginNode("A@1", "A@1");

        _guard.Observe(Tenant, "ByType", CkType, 10, null, 10);
        Assert.Empty(_logger.Warnings);

        _guard.Observe(Tenant, "ByType", CkType, 11, null, 11);
        Assert.Equal(10, Assert.Single(_logger.Warnings).State["MaxTake"]);
    }

    [Fact]
    public void Observe_Suppressed_LogsNothing()
    {
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using var node = RuntimeReadScope.BeginNode("A@1", "A@1");
        using var suppressed = RuntimeReadScope.Suppress();

        _guard.Observe(Tenant, "ByType", CkType, null, null, 5);

        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public void Observe_WithoutPipelineContext_ReportsUnknownCaller_OncePerTenantTypeAndHour()
    {
        Assert.Null(RuntimeReadScope.Current);

        _guard.Observe(Tenant, "ByType", CkType, null, null, 7);
        _guard.Observe(Tenant, "ByType", CkType, null, null, 7);
        _guard.Observe(Tenant, "ByType", "Test-1.0.0/Other", null, null, 7);

        Assert.Equal(2, _logger.Warnings.Count);
        var warning = _logger.Warnings[0];
        Assert.Equal("unknown", warning.State["NodeType"]);
        Assert.Equal("unknown", warning.State["NodePath"]);
        Assert.Equal("unknown", warning.State["PipelineId"]);
        Assert.Equal(1, CollectCount(Tenant, CkType, "unknown"));

        _time.Advance(RuntimeReadGuard.UnknownCallerInterval);
        _guard.Observe(Tenant, "ByType", CkType, null, null, 7);
        Assert.Equal(3, _logger.Warnings.Count);
    }

    /// <summary>
    ///     Lease invariant (AB#9824, pooled adapters): the throttle is the guard's only tenant-keyed state; forgetting
    ///     tenant A drops exactly A's entries, so nothing of A outlives its lease and B is untouched.
    /// </summary>
    [Fact]
    public void ForgetTenant_DropsOnlyThatTenantsThrottle()
    {
        const string other = "tenant-b";
        _guard.Observe(Tenant, "ByType", CkType, null, null, 7);
        _guard.Observe(other, "ByType", CkType, null, null, 7);
        Assert.Equal(2, _logger.Warnings.Count);

        _guard.ForgetTenant("TENANT-A");

        _guard.Observe(Tenant, "ByType", CkType, null, null, 7);
        _guard.Observe(other, "ByType", CkType, null, null, 7);
        var warnings = _logger.Warnings;
        Assert.Equal(3, warnings.Count);
        Assert.Equal(Tenant, warnings[2].State["TenantId"]);

        _guard.ForgetTenant("");
        _guard.ForgetTenant("unknown-tenant");
    }

    /// <summary>
    ///     Lease invariant (AB#9824): counter series are labelled per tenant, so a read of tenant A is never counted
    ///     as, or visible under, tenant B - also when both read the same CK type in the same node type.
    /// </summary>
    [Fact]
    public void Counter_SeriesOfTwoTenants_AreSeparate_AndNeverAttributedToTheOther()
    {
        const string other = "tenant-b";
        foreach (var (tenant, executionId, reads) in new[] { (Tenant, "e1", 1), (other, "e2", 1), (other, "e3", 1) })
        {
            using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(tenant, "p", null, "f",
                executionId));
            using var node = RuntimeReadScope.BeginNode("A@1", "A@1");
            for (var i = 0; i < reads; i++)
            {
                _guard.Observe(tenant, "ByType", CkType, null, null, 5);
            }
        }

        Assert.Equal(1, CollectCount(Tenant, CkType, "A@1"));
        Assert.Equal(2, CollectCount(other, CkType, "A@1"));
        Assert.All(_logger.Warnings, w => Assert.Contains(w.State["TenantId"], new object?[] { Tenant, other }));
    }

    [Fact]
    public void Counter_NewSeries_IsExportedAsZeroFirst_SoTheFirstHitIsAnIncrease()
    {
        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(Tenant, "p", null, "f", "e"));
        using var node = RuntimeReadScope.BeginNode("A@1", "A@1");
        _guard.Observe(Tenant, "ByType", CkType, null, null, 5);

        var collections = CollectSeries(2);

        Assert.Equal(0, collections[0].Single().Value);
        Assert.Equal(1, collections[1].Single().Value);
    }

    private long CollectCount(string tenant, string ckType, string nodeType)
    {
        // The first collection publishes new series as 0; the second has the real value.
        var collections = CollectSeries(2);
        return collections[1]
            .Where(m => Equals(m.Tags["tenant"], tenant) && Equals(m.Tags["ck_type"], ckType) &&
                        Equals(m.Tags["node_type"], nodeType) && Equals(m.Tags["mode"], "warn"))
            .Sum(m => m.Value);
    }

    private List<List<(long Value, Dictionary<string, object?> Tags)>> CollectSeries(int times)
    {
        var result = new List<List<(long, Dictionary<string, object?>)>>();
        List<(long, Dictionary<string, object?>)>? current = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == RuntimeReadGuardMetrics.UnboundedMetricName &&
                _meterFactory.Owns(instrument.Meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            current!.Add((value, dict));
        });
        listener.Start();
        for (var i = 0; i < times; i++)
        {
            current = new List<(long, Dictionary<string, object?>)>();
            listener.RecordObservableInstruments();
            result.Add(current);
        }

        return result;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly ConcurrentBag<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, this);
            _meters.Add(meter);
            return meter;
        }

        public bool Owns(Meter meter) => _meters.Contains(meter);

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Dictionary<string, object?> State);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = new();

        public List<LogEntry> Warnings
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Where(e => e.Level == LogLevel.Warning).ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var dict = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    dict[pair.Key] = pair.Value;
                }
            }

            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, eventId, dict));
            }
        }
    }
}
