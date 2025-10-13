using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents a version difference for a CkModel that exists in both tenants
/// </summary>
public class CkModelVersionDifference
{
    /// <summary>
    ///     Model identifier (without version)
    /// </summary>
    public string ModelId { get; set; } = null!;

    /// <summary>
    ///     Model version information from source tenant
    /// </summary>
    public CkModel SourceVersion { get; set; } = null!;

    /// <summary>
    ///     Model version information from target tenant
    /// </summary>
    public CkModel TargetVersion { get; set; } = null!;
}
