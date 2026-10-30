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
    /// <summary>
    /// Absolute ms-in-state with LOCF semantics (requires a comparison value). Supported over raw
    /// event archives and rollups that materialise a StateDuration aggregation. AB#4336/AB#4341.
    /// </summary>
    StateDuration,
    /// <summary>
    /// Step integral ∫v dt over the query window in value unit × hours (LOCF, carry-in from the
    /// last row before the window, rtIds summed). Raw event archives only for now (rollups:
    /// AB#7920). AB#7918.
    /// </summary>
    Integral,
}