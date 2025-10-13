namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents differences found between matched entities
/// </summary>
public class RtEntityDifference
{
    /// <summary>
    ///     Summary of the source entity
    /// </summary>
    public RtEntitySummary SourceEntity { get; set; } = null!;

    /// <summary>
    ///     Summary of the target entity
    /// </summary>
    public RtEntitySummary TargetEntity { get; set; } = null!;

    /// <summary>
    ///     Description of how entities were matched
    /// </summary>
    public string MatchedBy { get; set; } = null!;

    /// <summary>
    ///     List of property differences between the entities
    /// </summary>
    public List<PropertyDifference> PropertyDifferences { get; set; } = new();

    /// <summary>
    ///     Total number of property differences
    /// </summary>
    public int DifferenceCount => PropertyDifferences.Count;
}
