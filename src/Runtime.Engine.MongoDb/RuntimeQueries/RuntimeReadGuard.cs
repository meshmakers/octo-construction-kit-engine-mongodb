using System.Collections.Concurrent;

using Meshmakers.Octo.Runtime.Contracts.Repositories;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     Reports runtime-entity reads that may return more than <see cref="RuntimeReadGuardOptions.MaxTake" /> entities,
///     registered by <c>AddRuntimeReadGuard</c>. The tenant repositories call <see cref="Observe" /> after every runtime
///     read with its <c>take</c>, key count and the number of entities it returned. The guard never changes a read.
///     <para>
///         The caller comes from <see cref="RuntimeReadScope" />: inside a pipeline execution one warning and one count
///         per execution and node (<see cref="RuntimeReadExecution.TryMarkReported" />), however often the node runs.
///         Reads outside an execution (the host's own reads) are reported with caller <c>unknown</c>, at most once per
///         tenant, CK type and <see cref="UnknownCallerInterval" />. Reads inside <see cref="RuntimeReadScope.Suppress" />
///         are deliberate and not reported.
///     </para>
/// </summary>
internal sealed class RuntimeReadGuard
{
    /// <summary>How often a read without execution is reported per tenant and CK type.</summary>
    internal static readonly TimeSpan UnknownCallerInterval = TimeSpan.FromHours(1);

    /// <summary>The largest number of remembered unknown-caller keys; the oldest are dropped beyond it.</summary>
    internal const int MaxUnknownCallerKeys = 10_000;

    private const string Unknown = "unknown";

    private const string ReportedOnceHint = "Reported once per execution and node; the read was not changed. Give " +
                                            "the node a take (and page) or a filter that bounds it.";

    private readonly Func<RuntimeReadGuardOptions> _options;
    private readonly RuntimeReadGuardMetrics _metrics;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string Tenant, string CkType), DateTimeOffset> _unknownCallerReported = new();
    private readonly ConcurrentDictionary<string, byte> _reportedConfigurationErrors = new();

    public RuntimeReadGuard(IOptionsMonitor<RuntimeReadGuardOptions> options, RuntimeReadGuardMetrics metrics,
        ILogger<RuntimeReadGuard> logger)
        : this(() => options.CurrentValue, metrics, logger, TimeProvider.System)
    {
    }

    internal RuntimeReadGuard(Func<RuntimeReadGuardOptions> options, RuntimeReadGuardMetrics metrics, ILogger logger,
        TimeProvider timeProvider)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider;

        // Validate the configuration when the host first resolves the guard, so a wrong value is in the log at
        // startup and not only at the first unbounded read.
        ResolveSettings();
    }

    /// <summary>
    ///     The effective mode and limit. A configuration that names no <see cref="RuntimeReadGuardMode" />, or that
    ///     cannot be bound at all, is logged once per distinct value as a warning (event 41002) and the guard runs as
    ///     <see cref="RuntimeReadGuardMode.Warn" /> with the default limit: a misconfiguration never silently turns
    ///     the guard off. Never throws.
    /// </summary>
    internal (RuntimeReadGuardMode Mode, int MaxTake) ResolveSettings()
    {
        RuntimeReadGuardOptions options;
        try
        {
            options = _options();
        }
        catch (Exception e)
        {
            ReportConfigurationError("binding:" + e.GetType().Name + ":" + e.Message, () => _logger.LogWarning(
                RuntimeReadGuardEvents.InvalidConfiguration, e,
                "Runtime read guard configuration (section {Section}) cannot be bound: {Error}. Valid: Mode {ValidModes}, " +
                "MaxTake a positive integer. The guard runs as {FallbackMode} with MaxTake {FallbackMaxTake}; reads " +
                "are not changed.",
                RuntimeReadGuardOptions.SectionName, e.Message, ValidModes, RuntimeReadGuardMode.Warn,
                RuntimeReadGuardOptions.DefaultMaxTake));
            return (RuntimeReadGuardMode.Warn, RuntimeReadGuardOptions.DefaultMaxTake);
        }

        if (options.TryGetMode(out var mode))
        {
            return (mode, options.EffectiveMaxTake);
        }

        var value = options.Mode ?? string.Empty;
        ReportConfigurationError("mode:" + value, () => _logger.LogWarning(RuntimeReadGuardEvents.InvalidConfiguration,
            "Runtime read guard mode '{InvalidMode}' (configuration {Section}:Mode) is not valid. Valid: " +
            "{ValidModes}. The guard runs as {FallbackMode}; reads are not changed.",
            value, RuntimeReadGuardOptions.SectionName, ValidModes, RuntimeReadGuardMode.Warn));
        return (RuntimeReadGuardMode.Warn, options.EffectiveMaxTake);
    }

    private static string ValidModes => string.Join(" | ", Enum.GetNames<RuntimeReadGuardMode>());

    private void ReportConfigurationError(string key, Action log)
    {
        try
        {
            if (_reportedConfigurationErrors.TryAdd(key, 0))
            {
                log();
            }
        }
        catch
        {
            // Observability only.
        }
    }

    /// <summary>
    ///     True when a read with <paramref name="take" /> and <paramref name="keyCount" /> may return more than
    ///     <paramref name="maxTake" /> entities.
    /// </summary>
    /// <param name="take">The take of the read, or null.</param>
    /// <param name="keyCount">The number of ids a by-id read is limited to, or null for a read by type.</param>
    /// <param name="maxTake">The limit.</param>
    internal static bool IsUnbounded(int? take, int? keyCount, int maxTake)
    {
        if (take is >= 0 && take.Value <= maxTake)
        {
            return false;
        }

        return keyCount is not { } keys || keys > maxTake;
    }

    /// <summary>
    ///     True when <see cref="Observe" /> may report a read with <paramref name="take" /> and
    ///     <paramref name="keyCount" />: the guard is on and the read is unbounded. Lets the repositories skip counting
    ///     the result of every bounded read. Never throws.
    /// </summary>
    public bool IsCandidate(int? take, int? keyCount)
    {
        var (mode, maxTake) = ResolveSettings();
        return mode != RuntimeReadGuardMode.Off && IsUnbounded(take, keyCount, maxTake);
    }

    /// <summary>
    ///     Reports the read when it is unbounded (see the class). Never throws: a failure is logged at debug level.
    /// </summary>
    /// <param name="tenantId">The tenant of the repository.</param>
    /// <param name="readKind">The read method, e.g. <c>ByType</c>, <c>GraphByType</c>, <c>AssociationTargets</c>.</param>
    /// <param name="ckTypeId">The CK type that is read (for association reads the target type).</param>
    /// <param name="take">The take of the read, or null.</param>
    /// <param name="keyCount">The number of ids a by-id read is limited to, or null.</param>
    /// <param name="returnedCount">The number of entities the read returned, or null when it is not known without enumerating the result.</param>
    public void Observe(string tenantId, string readKind, string ckTypeId, int? take, int? keyCount,
        long? returnedCount)
    {
        try
        {
            var (mode, maxTake) = ResolveSettings();
            if (mode == RuntimeReadGuardMode.Off)
            {
                return;
            }

            if (!IsUnbounded(take, keyCount, maxTake))
            {
                return;
            }

            var caller = RuntimeReadScope.Current;
            if (caller is { Suppressed: true })
            {
                return;
            }

            if (!ShouldReport(caller, tenantId, ckTypeId))
            {
                return;
            }

            var execution = caller?.Execution;
            if (execution?.PipelineName is { } pipelineName)
            {
                _logger.LogWarning(RuntimeReadGuardEvents.UnboundedRead,
                    "Unbounded runtime-entity read ({ReadKind}) on CK type {CkTypeId} in tenant {TenantId}: take " +
                    "{Take}, {ReturnedCount} entities returned, limit {MaxTake}; node {NodeType} at {NodePath}, " +
                    "pipeline {PipelineId} ({PipelineName}), data flow {DataFlowId}, execution {ExecutionId}. " +
                    ReportedOnceHint,
                    readKind, ckTypeId, tenantId, FormatTake(take), (object?)returnedCount ?? Unknown, maxTake,
                    caller?.NodeType ?? Unknown, caller?.NodePath ?? Unknown,
                    execution.PipelineId ?? Unknown, pipelineName,
                    execution.DataFlowId ?? Unknown, execution.ExecutionId ?? Unknown);
            }
            else
            {
                // The pipeline name is optional (the mesh adapter's pipeline contract carries only the ids), so the
                // message and the structured properties leave it out instead of logging a placeholder.
                _logger.LogWarning(RuntimeReadGuardEvents.UnboundedRead,
                    "Unbounded runtime-entity read ({ReadKind}) on CK type {CkTypeId} in tenant {TenantId}: take " +
                    "{Take}, {ReturnedCount} entities returned, limit {MaxTake}; node {NodeType} at {NodePath}, " +
                    "pipeline {PipelineId}, data flow {DataFlowId}, execution {ExecutionId}. " + ReportedOnceHint,
                    readKind, ckTypeId, tenantId, FormatTake(take), (object?)returnedCount ?? Unknown, maxTake,
                    caller?.NodeType ?? Unknown, caller?.NodePath ?? Unknown,
                    execution?.PipelineId ?? Unknown,
                    execution?.DataFlowId ?? Unknown, execution?.ExecutionId ?? Unknown);
            }

            _metrics.RecordUnbounded(tenantId, ckTypeId, caller?.NodeType, "warn");
        }
        catch (Exception e)
        {
            // Observability only: a failure here must never fail the read.
            _logger.LogDebug(e, "Runtime read guard failed for CK type {CkTypeId} in tenant {TenantId}", ckTypeId,
                tenantId);
        }
    }

    private bool ShouldReport(RuntimeReadCaller? caller, string tenantId, string ckTypeId)
    {
        if (caller?.Execution is { } execution)
        {
            return execution.TryMarkReported(caller.NodeKey);
        }

        var now = _timeProvider.GetUtcNow();
        var key = (tenantId, ckTypeId);
        while (true)
        {
            if (_unknownCallerReported.TryGetValue(key, out var last))
            {
                if (now - last < UnknownCallerInterval)
                {
                    return false;
                }

                if (_unknownCallerReported.TryUpdate(key, now, last))
                {
                    return true;
                }

                continue;
            }

            if (_unknownCallerReported.Count >= MaxUnknownCallerKeys)
            {
                PruneUnknownCallers(now);
            }

            if (_unknownCallerReported.TryAdd(key, now))
            {
                return true;
            }
        }
    }

    /// <summary>
    ///     Forgets the unknown-caller throttle of a tenant, the guard's only tenant-keyed state, so nothing of the
    ///     tenant outlives its lease on a pooled adapter or its deletion (AB#9824). Called by
    ///     <c>ISystemContext.InvalidateTenantResolveImportGuards</c>. The next read of the tenant without execution is
    ///     reported again. The counter series of <see cref="RuntimeReadGuardMetrics" /> are kept: they carry the tenant
    ///     only as a label and are cumulative by design. Never throws.
    /// </summary>
    internal void ForgetTenant(string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return;
        }

        foreach (var key in _unknownCallerReported.Keys)
        {
            if (string.Equals(key.Tenant, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                _unknownCallerReported.TryRemove(key, out _);
            }
        }
    }

    private void PruneUnknownCallers(DateTimeOffset now)
    {
        foreach (var (key, last) in _unknownCallerReported)
        {
            if (now - last >= UnknownCallerInterval)
            {
                _unknownCallerReported.TryRemove(key, out _);
            }
        }

        if (_unknownCallerReported.Count >= MaxUnknownCallerKeys)
        {
            _unknownCallerReported.Clear();
        }
    }

    private static string FormatTake(int? take) => take switch
    {
        null => "null",
        int.MaxValue => "int.MaxValue",
        _ => take.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}

/// <summary>Event ids of the runtime read guard.</summary>
internal static class RuntimeReadGuardEvents
{
    /// <summary>An unbounded runtime-entity read was reported.</summary>
    public static readonly EventId UnboundedRead = new(41001, "RuntimeReadGuard.UnboundedRead");

    /// <summary>The configuration names no valid mode or cannot be bound; the guard runs as Warn.</summary>
    public static readonly EventId InvalidConfiguration = new(41002, "RuntimeReadGuard.InvalidConfiguration");
}

/// <summary>The read kinds the tenant repositories report (<c>ReadKind</c> of the warning).</summary>
internal static class RuntimeReadKinds
{
    /// <summary>Entities of one CK type (<c>GetRtEntitiesByTypeAsync</c>).</summary>
    public const string ByType = "ByType";

    /// <summary>Entities by id (<c>GetRtEntitiesByIdAsync</c>); bounded by its key list up to the limit.</summary>
    public const string ById = "ById";

    /// <summary>Entities of one CK type with navigation (<c>GetRtEntitiesGraphByTypeAsync</c>; persistent queries).</summary>
    public const string GraphByType = "GraphByType";

    /// <summary>Entities by id with navigation (<c>GetRtEntitiesGraphByIdAsync</c>).</summary>
    public const string GraphById = "GraphById";

    /// <summary>Direct association targets of a set of origins (<c>GetRtAssociationTargetsAsync</c>).</summary>
    public const string AssociationTargets = "AssociationTargets";

    /// <summary>Indirect association targets of a set of origins (<c>GetIndirectRtAssociationTargetsAsync</c>).</summary>
    public const string IndirectAssociationTargets = "IndirectAssociationTargets";

    /// <summary>A deep graph below a set of origins (<c>GetRtDeepGraphAsync</c>).</summary>
    public const string DeepGraph = "DeepGraph";
}
