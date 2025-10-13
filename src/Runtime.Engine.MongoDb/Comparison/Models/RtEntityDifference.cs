using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents differences found between matched entities
/// </summary>
public class RtEntityDifference
{
    /// <summary>
    ///     The source entity
    /// </summary>
    public RtEntity SourceEntity { get; set; } = null!;

    /// <summary>
    ///     The target entity
    /// </summary>
    public RtEntity TargetEntity { get; set; } = null!;

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
