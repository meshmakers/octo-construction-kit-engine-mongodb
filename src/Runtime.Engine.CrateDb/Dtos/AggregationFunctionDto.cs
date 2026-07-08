namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

/// <summary>
/// Aggregation Functions
/// </summary>
public enum AggregationFunctionDto
{
    /// <summary>
    /// Average
    /// </summary>
    Avg,
    /// <summary>
    /// Minimum
    /// </summary>
    Min,
    /// <summary>
    /// Maximum
    /// </summary>
    Max,
    /// <summary>
    /// Count
    /// </summary>
    Count,
    /// <summary>
    /// Sum
    /// </summary>
    Sum,
    /// <summary>
    /// Time-weighted average (LOCF interval weighting). Resolvable against rollup archives that
    /// materialise a TimeWeightedAvg aggregation (integral/duration pair). AB#4336.
    /// </summary>
    TimeWeightedAvg,
}