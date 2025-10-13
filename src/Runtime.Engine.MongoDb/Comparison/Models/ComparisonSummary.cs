namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Overall summary of tenant comparison results
/// </summary>
public class ComparisonSummary
{
    /// <summary>
    ///     Total number of differences found across all comparison areas
    /// </summary>
    public int TotalDifferences { get; set; }

    /// <summary>
    ///     Indicates whether the tenants are identical (no differences found)
    /// </summary>
    public bool AreIdentical => TotalDifferences == 0;

    /// <summary>
    ///     Number of CkModel differences
    /// </summary>
    public int CkModelDifferences { get; set; }

    /// <summary>
    ///     Number of CkType differences
    /// </summary>
    public int CkTypeDifferences { get; set; }

    /// <summary>
    ///     Number of RtEntity differences
    /// </summary>
    public int RtEntityDifferences { get; set; }

    /// <summary>
    ///     Number of association differences
    /// </summary>
    public int AssociationDifferences { get; set; }

    /// <summary>
    ///     Number of metadata differences
    /// </summary>
    public int MetadataDifferences { get; set; }
}
