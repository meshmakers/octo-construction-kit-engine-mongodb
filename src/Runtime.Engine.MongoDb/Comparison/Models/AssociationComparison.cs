using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Results of association comparison between two tenants
/// </summary>
public class AssociationComparison
{
    /// <summary>
    ///     Total association count in source tenant
    /// </summary>
    public long SourceTotalCount { get; set; }

    /// <summary>
    ///     Total association count in target tenant
    /// </summary>
    public long TargetTotalCount { get; set; }

    /// <summary>
    ///     Associations present only in source tenant
    /// </summary>
    public List<RtAssociation> OnlyInSource { get; set; } = new();

    /// <summary>
    ///     Associations present only in target tenant
    /// </summary>
    public List<RtAssociation> OnlyInTarget { get; set; } = new();

    /// <summary>
    ///     Number of associations present in both tenants
    /// </summary>
    public int MatchedAssociationCount { get; set; }

    /// <summary>
    ///     Total number of association differences
    /// </summary>
    public int TotalDifferences => OnlyInSource.Count + OnlyInTarget.Count;
}
