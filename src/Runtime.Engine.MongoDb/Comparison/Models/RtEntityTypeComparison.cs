using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Comparison results for entities of a specific CkType
/// </summary>
public class RtEntityTypeComparison
{
    /// <summary>
    ///     Construction Kit type identifier
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; set; } = null!;

    /// <summary>
    ///     Total entity count in source tenant (before filtering)
    /// </summary>
    public long SourceTotalCount { get; set; }

    /// <summary>
    ///     Total entity count in target tenant (before filtering)
    /// </summary>
    public long TargetTotalCount { get; set; }

    /// <summary>
    ///     Number of entities included in comparison from source (after filtering)
    /// </summary>
    public int SourceFilteredCount { get; set; }

    /// <summary>
    ///     Number of entities included in comparison from target (after filtering)
    /// </summary>
    public int TargetFilteredCount { get; set; }

    /// <summary>
    ///     Entities present only in source tenant
    /// </summary>
    public List<RtEntity> OnlyInSource { get; set; } = new();

    /// <summary>
    ///     Entities present only in target tenant
    /// </summary>
    public List<RtEntity> OnlyInTarget { get; set; } = new();

    /// <summary>
    ///     Matched entities with detected differences
    /// </summary>
    public List<RtEntityDifference> Differences { get; set; } = new();

    /// <summary>
    ///     Number of matched entities with no differences
    /// </summary>
    public int MatchedIdenticalCount { get; set; }

    /// <summary>
    ///     Total number of differences for this type
    /// </summary>
    public int TotalDifferences => OnlyInSource.Count + OnlyInTarget.Count + Differences.Count;
}
