using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents a version difference for a CkModel that exists in both tenants
/// </summary>
public class CkModelVersionDifference
{
    /// <summary>
    ///     Model identifier key (namespace/name, without version)
    /// </summary>
    public string ModelKey { get; set; } = null!;

    /// <summary>
    ///     Model version information from source tenant
    /// </summary>
    public CkModelId SourceVersion { get; set; } = null!;

    /// <summary>
    ///     Model version information from target tenant
    /// </summary>
    public CkModelId TargetVersion { get; set; } = null!;
}
