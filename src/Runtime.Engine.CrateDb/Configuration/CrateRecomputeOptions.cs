namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;

/// <summary>
/// Tunables of the CrateDB statements a rollup recompute and the rollup aggregation send
/// (<see cref="CrateStatementClass.Maintenance"/>). Bound from the <see cref="SectionName"/>
/// configuration section; the host's recompute orchestrator options live in the same section under
/// other property names.
/// </summary>
public sealed class CrateRecomputeOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "StreamData:Recompute";

    /// <summary>Default of <see cref="SliceTargetRows"/>.</summary>
    public const int DefaultSliceTargetRows = 100_000;

    /// <summary>Default of <see cref="CommandTimeout"/>.</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Rows one copy, sweep or reclaim statement of a recompute aims at; a statement holds at most
    /// twice that, whatever the size of the recomputed range. Also the threshold up to which the
    /// staging table is emptied and kept instead of dropped. Must be positive. Default 100,000.
    /// </summary>
    public int SliceTargetRows { get; set; } = DefaultSliceTargetRows;

    /// <summary>
    /// Command timeout of every <see cref="CrateStatementClass.Maintenance"/> statement. Must be at
    /// least one second. Default two minutes.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = DefaultCommandTimeout;

    /// <summary>True when the values are usable; checked when the options are first read.</summary>
    internal bool IsValid() => SliceTargetRows > 0 && CommandTimeout >= TimeSpan.FromSeconds(1);

    internal const string ValidationMessage =
        "StreamData:Recompute:SliceTargetRows must be greater than 0 and " +
        "StreamData:Recompute:CommandTimeout must be at least one second.";
}
