namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Results of metadata comparison between two tenants
/// </summary>
public class MetadataComparison
{
    /// <summary>
    ///     Source tenant metadata
    /// </summary>
    public TenantMetadata Source { get; set; } = null!;

    /// <summary>
    ///     Target tenant metadata
    /// </summary>
    public TenantMetadata Target { get; set; } = null!;

    /// <summary>
    ///     List of detected metadata differences
    /// </summary>
    public List<MetadataDifference> Differences { get; set; } = new();
}
