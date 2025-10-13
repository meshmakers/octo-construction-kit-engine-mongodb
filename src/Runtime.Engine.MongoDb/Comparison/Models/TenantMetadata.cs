namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Metadata information about a tenant
/// </summary>
public class TenantMetadata
{
    /// <summary>
    ///     Tenant identifier
    /// </summary>
    public string TenantId { get; set; } = null!;

    /// <summary>
    ///     Database name used by the tenant
    /// </summary>
    public string DatabaseName { get; set; } = null!;

    /// <summary>
    ///     Total number of RtEntities across all CkTypes
    /// </summary>
    public long TotalRtEntityCount { get; set; }

    /// <summary>
    ///     RtEntity count grouped by CkType identifier
    /// </summary>
    public Dictionary<string, long> RtEntityCountByCkType { get; set; } = new();

    /// <summary>
    ///     Total number of associations
    /// </summary>
    public long TotalAssociationCount { get; set; }

    /// <summary>
    ///     Number of imported CkModels
    /// </summary>
    public long CkModelCount { get; set; }
}
