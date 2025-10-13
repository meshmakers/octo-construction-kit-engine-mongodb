using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents a difference found in a CkType between source and target tenants
/// </summary>
public class CkTypeDifference
{
    /// <summary>
    ///     The CkTypeId that has differences
    /// </summary>
    public string CkTypeId { get; set; } = null!;

    /// <summary>
    ///     The CkType from the source tenant
    /// </summary>
    public CkType SourceType { get; set; } = null!;

    /// <summary>
    ///     The CkType from the target tenant
    /// </summary>
    public CkType TargetType { get; set; } = null!;

    /// <summary>
    ///     Description of the difference
    /// </summary>
    public string Description { get; set; } = null!;
}
