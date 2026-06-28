namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

/// <summary>
/// Defines the kind of query to be executed
/// </summary>
public enum QueryModeDto
{
    /// <summary>
    /// Returns the raw stored values (no reduction).
    /// </summary>
    Default,

    /// <summary>
    /// Reduces the result to <c>Limit</c> equal-width time buckets. Honoured by every aggregating
    /// stream-data variant and — since AB#4233 — also by a <c>SimpleSdQuery</c> (and the transient
    /// <c>simple</c> sub-connection): the engine then synthesizes a per-value-type reducer for each
    /// projected column (AVG+MIN+MAX for numeric, MAX for the rest) and groups per source rtId, so
    /// raw-row queries get a representative N-point reduction without a separate DownsamplingSdQuery.
    /// </summary>
    Downsampling,
    
    /// <summary>
    /// Interpolates the values
    /// </summary>
    Interpolation
}