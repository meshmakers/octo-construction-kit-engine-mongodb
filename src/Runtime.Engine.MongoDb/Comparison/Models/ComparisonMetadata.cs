namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Metadata about the comparison operation itself
/// </summary>
public class ComparisonMetadata
{
    /// <summary>
    ///     Timestamp when comparison was performed
    /// </summary>
    public DateTimeOffset ComparisonDate { get; set; }

    /// <summary>
    ///     Source tenant identifier
    /// </summary>
    public string SourceTenantId { get; set; } = null!;

    /// <summary>
    ///     Target tenant identifier
    /// </summary>
    public string TargetTenantId { get; set; } = null!;

    /// <summary>
    ///     Description of comparison options used
    /// </summary>
    public string OptionsDescription { get; set; } = null!;

    /// <summary>
    ///     Description of entity matching strategy used (if applicable)
    /// </summary>
    public string? MatchingStrategyDescription { get; set; }

    /// <summary>
    ///     Description of filter strategy used (if applicable)
    /// </summary>
    public string? FilterStrategyDescription { get; set; }

    /// <summary>
    ///     Duration of the comparison operation
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    ///     Indicates whether the source tenant was restored from a backup for this comparison
    /// </summary>
    public bool SourceWasRestoredFromBackup { get; set; }

    /// <summary>
    ///     Indicates whether the target tenant was restored from a backup for this comparison
    /// </summary>
    public bool TargetWasRestoredFromBackup { get; set; }

    /// <summary>
    ///     File path to the source backup archive if source was restored from backup
    /// </summary>
    public string? SourceBackupPath { get; set; }

    /// <summary>
    ///     File path to the target backup archive if target was restored from backup
    /// </summary>
    public string? TargetBackupPath { get; set; }
}
