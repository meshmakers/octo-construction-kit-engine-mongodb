using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Compares associations between two tenants
/// </summary>
internal class AssociationComparator
{
    /// <summary>
    ///     Compares association data from source and target tenants
    /// </summary>
    /// <param name="source">Source tenant association data</param>
    /// <param name="target">Target tenant association data</param>
    /// <returns>Association comparison results</returns>
    public AssociationComparison Compare(AssociationData source, AssociationData target)
    {
        var comparison = new AssociationComparison
        {
            SourceTotalCount = source.TotalCount,
            TargetTotalCount = target.TotalCount
        };

        // Create lookup sets for efficient comparison
        // Key format: "RoleId|OriginRtId|TargetRtId"
        var sourceSet = new HashSet<string>(source.Associations.Select(GetAssociationKey));
        var targetSet = new HashSet<string>(target.Associations.Select(GetAssociationKey));

        // Find associations only in source
        foreach (RtAssociation assoc in source.Associations)
        {
            if (!targetSet.Contains(GetAssociationKey(assoc)))
            {
                comparison.OnlyInSource.Add(assoc);
            }
        }

        // Find associations only in target
        foreach (RtAssociation assoc in target.Associations)
        {
            if (!sourceSet.Contains(GetAssociationKey(assoc)))
            {
                comparison.OnlyInTarget.Add(assoc);
            }
        }

        // Count matched associations (present in both)
        comparison.MatchedAssociationCount = sourceSet.Intersect(targetSet).Count();

        return comparison;
    }

    /// <summary>
    ///     Creates a unique key for an association based on its role and connected entities
    /// </summary>
    private string GetAssociationKey(RtAssociation assoc)
    {
        return $"{assoc.AssociationRoleId}|{assoc.OriginRtId}|{assoc.TargetRtId}";
    }
}
