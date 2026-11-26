using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     The counter of the runtime read guard: <c>octo.engine.runtime_read.unbounded</c> (Prometheus form
///     <c>octo_engine_runtime_read_unbounded_total</c>) on the meter <see cref="MongoCommandObservability.MeterName" />,
///     which every host that calls <c>AddObservability()</c> exports. Labels: <c>tenant</c>, <c>ck_type</c>,
///     <c>node_type</c> (<c>unknown</c> outside a pipeline node) and <c>mode</c>; all bounded by the tenants, CK types and
///     node types of an installation, and capped at <see cref="MaxSeries" /> series.
/// </summary>
/// <remarks>
///     <b>The first hit is countable.</b> A plain counter exports a series only from its first increment, so a rate or
///     <c>increase()</c> over a series that starts at 1 shows 0 for that first hit. This counter is observable: a series
///     is exported as 0 at the first collection after its first hit and with its real value from the next collection
///     on, so every hit appears as an increase. The value lags one export interval behind the log line.
/// </remarks>
internal sealed class RuntimeReadGuardMetrics
{
    /// <summary>Counter name. Contract with Dash0 queries; must not change.</summary>
    public const string UnboundedMetricName = "octo.engine.runtime_read.unbounded";

    /// <summary>Label names.</summary>
    public const string TenantLabel = "tenant";

    /// <inheritdoc cref="TenantLabel" />
    public const string CkTypeLabel = "ck_type";

    /// <inheritdoc cref="TenantLabel" />
    public const string NodeTypeLabel = "node_type";

    /// <inheritdoc cref="TenantLabel" />
    public const string ModeLabel = "mode";

    /// <summary>Label value of <see cref="NodeTypeLabel" /> for a read outside a pipeline node.</summary>
    public const string UnknownNodeType = "unknown";

    /// <summary>Label value of every label once <see cref="MaxSeries" /> series exist.</summary>
    public const string OtherLabelValue = "other";

    /// <summary>The largest number of series; further label combinations are counted under <see cref="OtherLabelValue" />.</summary>
    public const int MaxSeries = 2000;

    private readonly ConcurrentDictionary<SeriesKey, Series> _series = new();

    public RuntimeReadGuardMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(new MeterOptions(MongoCommandObservability.MeterName) { Version = "1.0.0" });
        meter.CreateObservableCounter(
            UnboundedMetricName,
            Observe,
            unit: "{read}",
            description: "Runtime-entity reads through the engine (pipeline nodes) without take or with take above " +
                         "RuntimeReadGuard:MaxTake, once per execution and node.");
    }

    /// <summary>Counts one unbounded read.</summary>
    public void RecordUnbounded(string tenantId, string ckTypeId, string? nodeType, string mode)
    {
        var key = new SeriesKey(tenantId, ckTypeId, string.IsNullOrEmpty(nodeType) ? UnknownNodeType : nodeType, mode);
        if (!_series.TryGetValue(key, out var series))
        {
            if (_series.Count >= MaxSeries)
            {
                key = new SeriesKey(OtherLabelValue, OtherLabelValue, OtherLabelValue, mode);
            }

            series = _series.GetOrAdd(key, _ => new Series());
        }

        Interlocked.Increment(ref series.Count);
    }

    private IEnumerable<Measurement<long>> Observe()
    {
        foreach (var (key, series) in _series)
        {
            var value = Interlocked.Exchange(ref series.Published, 1) == 0 ? 0 : Interlocked.Read(ref series.Count);
            yield return new Measurement<long>(value,
                new KeyValuePair<string, object?>(TenantLabel, key.Tenant),
                new KeyValuePair<string, object?>(CkTypeLabel, key.CkType),
                new KeyValuePair<string, object?>(NodeTypeLabel, key.NodeType),
                new KeyValuePair<string, object?>(ModeLabel, key.Mode));
        }
    }

    private readonly record struct SeriesKey(string Tenant, string CkType, string NodeType, string Mode);

    private sealed class Series
    {
        public long Count;
        public int Published;
    }
}
