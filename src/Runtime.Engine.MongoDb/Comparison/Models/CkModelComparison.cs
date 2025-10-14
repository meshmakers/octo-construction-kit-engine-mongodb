using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Results of CkModel comparison between two tenants
/// </summary>
public class CkModelComparison
{
    /// <summary>
    ///     Models present only in source tenant
    /// </summary>
    public List<CkModelId> OnlyInSource { get; set; } = new();

    /// <summary>
    ///     Models present only in target tenant
    /// </summary>
    public List<CkModelId> OnlyInTarget { get; set; } = new();

    /// <summary>
    ///     Models present in both tenants with identical versions
    /// </summary>
    public List<CkModelId> InBothSameVersion { get; set; } = new();

    /// <summary>
    ///     Models present in both tenants with different versions
    /// </summary>
    public List<CkModelVersionDifference> VersionDifferences { get; set; } = new();

    /// <summary>
    ///     Total number of model differences
    /// </summary>
    public int TotalDifferences => OnlyInSource.Count + OnlyInTarget.Count + VersionDifferences.Count;
}
