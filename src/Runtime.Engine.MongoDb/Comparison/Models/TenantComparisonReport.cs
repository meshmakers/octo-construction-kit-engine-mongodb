namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Comprehensive tenant comparison report containing all comparison results
/// </summary>
public class TenantComparisonReport
{
    /// <summary>
    ///     Metadata about the comparison operation
    /// </summary>
    public ComparisonMetadata Metadata { get; set; } = null!;

    /// <summary>
    ///     Tenant metadata comparison results (if requested)
    /// </summary>
    public MetadataComparison? MetadataComparison { get; set; }

    /// <summary>
    ///     CkModel comparison results (if requested)
    /// </summary>
    public CkModelComparison? CkModelComparison { get; set; }

    /// <summary>
    ///     CkType comparison results (if requested)
    /// </summary>
    public CkTypeComparison? CkTypeComparison { get; set; }

    /// <summary>
    ///     RtEntity comparison results grouped by CkType (if requested)
    /// </summary>
    public Dictionary<string, RtEntityTypeComparison>? RtEntityComparisons { get; set; }

    /// <summary>
    ///     Association comparison results (if requested)
    /// </summary>
    public AssociationComparison? AssociationComparison { get; set; }

    /// <summary>
    ///     Overall comparison summary
    /// </summary>
    public ComparisonSummary Summary { get; set; } = null!;
}
