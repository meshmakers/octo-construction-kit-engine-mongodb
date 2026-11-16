using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     The host's opt-in to the runtime query time limit (AB#8897), registered by <c>AddRuntimeQueryTimeLimit</c>. The
///     tenant repositories ask for it with <c>GetService</c>: a host without it runs runtime queries without a limit.
///     Reads <see cref="RuntimeQueryOptions" /> per query, so a reloaded configuration applies to the next query.
/// </summary>
internal sealed class RuntimeQueryTimeLimitSettings
{
    private readonly Func<TimeSpan?> _maxTime;

    /// <summary>
    ///     The settings of the host configuration.
    /// </summary>
    public RuntimeQueryTimeLimitSettings(IOptionsMonitor<RuntimeQueryOptions> options)
    {
        _maxTime = () => options.CurrentValue.MaxTime;
    }

    /// <summary>
    ///     Fixed settings (tests).
    /// </summary>
    internal RuntimeQueryTimeLimitSettings(TimeSpan? maxTime)
    {
        _maxTime = () => maxTime;
    }

    /// <summary>
    ///     The configured limit, or null when it is switched off (<see cref="RuntimeQueryOptions.MaxTimeMs" /> 0).
    /// </summary>
    public TimeSpan? MaxTime => _maxTime();
}
