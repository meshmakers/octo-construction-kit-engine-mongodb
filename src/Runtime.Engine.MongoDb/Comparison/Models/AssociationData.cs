using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
/// Encapsulates association data retrieved from a tenant for comparison purposes
/// </summary>
public class AssociationData
{
    /// <summary>
    /// All associations in the tenant
    /// </summary>
    public List<RtAssociation> Associations { get; set; } = new();

    /// <summary>
    /// Total count of associations
    /// </summary>
    public int TotalCount => Associations.Count;
}
