using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
/// Encapsulates entity data retrieved from a tenant for comparison purposes
/// </summary>
public class TenantEntityData
{
    /// <summary>
    /// Total count of entities of this type in the tenant (before filtering)
    /// </summary>
    public long TotalCount { get; set; }

    /// <summary>
    /// Entities that passed the filter criteria
    /// </summary>
    public List<RtEntity> FilteredEntities { get; set; } = new();

    /// <summary>
    /// Number of filtered entities (convenience property)
    /// </summary>
    public int FilteredCount => FilteredEntities.Count;
}
