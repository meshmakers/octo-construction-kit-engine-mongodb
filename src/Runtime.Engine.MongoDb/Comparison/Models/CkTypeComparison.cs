using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Results of CkType comparison between two tenants
/// </summary>
public class CkTypeComparison
{
    /// <summary>
    ///     Types present only in source tenant
    /// </summary>
    public List<CkType> OnlyInSource { get; set; } = new();

    /// <summary>
    ///     Types present only in target tenant
    /// </summary>
    public List<CkType> OnlyInTarget { get; set; } = new();

    /// <summary>
    ///     Types present in both tenants with identical properties
    /// </summary>
    public List<CkType> InBothSame { get; set; } = new();

    /// <summary>
    ///     Types present in both tenants with different properties
    /// </summary>
    public List<CkTypeDifference> Differences { get; set; } = new();

    /// <summary>
    ///     Total number of type differences
    /// </summary>
    public int TotalDifferences => OnlyInSource.Count + OnlyInTarget.Count + Differences.Count;
}
