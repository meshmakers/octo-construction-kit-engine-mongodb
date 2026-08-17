namespace Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;

/// <summary>
///     Options for the display-rule backfill sweep background service (AB#4812).
///     Bound from configuration section <c>DisplayRules:Sweep</c>.
/// </summary>
public sealed class DisplayRuleSweepOptions
{
    /// <summary>Configuration section the options are bound from.</summary>
    public const string SectionName = "DisplayRules:Sweep";

    /// <summary>Delay before the first tick after service start.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Interval between ticks; each tick drains all currently due sweep tasks.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Page size for the entity sweep.</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>Duration of the single-flight claim lease per task.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Minimum interval between attempts of the same task.</summary>
    public TimeSpan MinRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Retry budget per task; exhausted tasks stay listed for operators.</summary>
    public int MaxAttempts { get; set; } = 10;
}
