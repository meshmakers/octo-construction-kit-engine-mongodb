using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Comparator for CkType comparison between tenants
/// </summary>
internal class CkTypeComparator
{
    /// <summary>
    ///     Compares CkTypeGraph lists from source and target tenants
    /// </summary>
    /// <param name="sourceTypes">Source tenant CkTypeGraphs</param>
    /// <param name="targetTypes">Target tenant CkTypeGraphs</param>
    /// <returns>CkType comparison results with identified differences</returns>
    public CkTypeComparison Compare(List<CkTypeGraph> sourceTypes, List<CkTypeGraph> targetTypes)
    {
        var comparison = new CkTypeComparison();

        // Create dictionaries for quick lookup by CkTypeId
        var sourceByTypeId = sourceTypes.ToDictionary(t => t.CkTypeId.ToString());
        var targetByTypeId = targetTypes.ToDictionary(t => t.CkTypeId.ToString());

        // Get all unique CkTypeIds
        var allTypeIds = new HashSet<string>(sourceByTypeId.Keys);
        foreach (string typeId in targetByTypeId.Keys)
        {
            allTypeIds.Add(typeId);
        }

        // Compare each CkTypeId
        foreach (string typeId in allTypeIds)
        {
            bool inSource = sourceByTypeId.TryGetValue(typeId, out CkTypeGraph? sourceType);
            bool inTarget = targetByTypeId.TryGetValue(typeId, out CkTypeGraph? targetType);

            if (inSource && !inTarget)
            {
                // Type exists only in source
                comparison.OnlyInSource.Add(sourceType!);
            }
            else if (!inSource && inTarget)
            {
                // Type exists only in target
                comparison.OnlyInTarget.Add(targetType!);
            }
            else if (inSource && inTarget)
            {
                // Type exists in both - compare properties
                if (AreTypesIdentical(sourceType!, targetType!))
                {
                    comparison.InBothSame.Add(sourceType!);
                }
                else
                {
                    comparison.Differences.Add(new CkTypeDifference
                    {
                        CkTypeId = typeId,
                        SourceType = sourceType!,
                        TargetType = targetType!,
                        Description = BuildDifferenceDescription(sourceType!, targetType!)
                    });
                }
            }
        }

        return comparison;
    }

    private bool AreTypesIdentical(CkTypeGraph source, CkTypeGraph target)
    {
        // Compare key properties
        if (source.IsFinal != target.IsFinal) return false;
        if (source.IsAbstract != target.IsAbstract) return false;
        if (source.Description != target.Description) return false;
        if (source.IsCollectionRoot != target.IsCollectionRoot) return false;
        if (source.IsStreamType != target.IsStreamType) return false;
        if (source.DerivedFromCkTypeId != target.DerivedFromCkTypeId) return false;

        // Compare attribute count (simple comparison)
        if (source.AllAttributes.Count != target.AllAttributes.Count) return false;

        // Compare associations count
        if (source.Associations.In.All.Count + source.Associations.Out.All.Count !=
            target.Associations.In.All.Count + target.Associations.Out.All.Count) return false;

        // Compare indexes count
        if (source.Indexes.Count != target.Indexes.Count) return false;

        return true;
    }

    private string BuildDifferenceDescription(CkTypeGraph source, CkTypeGraph target)
    {
        var differences = new List<string>();

        if (source.IsFinal != target.IsFinal)
        {
            differences.Add($"IsFinal: {source.IsFinal} vs {target.IsFinal}");
        }

        if (source.IsAbstract != target.IsAbstract)
        {
            differences.Add($"IsAbstract: {source.IsAbstract} vs {target.IsAbstract}");
        }

        if (source.Description != target.Description)
        {
            differences.Add($"Description differs");
        }

        if (source.IsCollectionRoot != target.IsCollectionRoot)
        {
            differences.Add($"IsCollectionRoot: {source.IsCollectionRoot} vs {target.IsCollectionRoot}");
        }

        if (source.IsStreamType != target.IsStreamType)
        {
            differences.Add($"IsStreamType: {source.IsStreamType} vs {target.IsStreamType}");
        }

        if (source.DerivedFromCkTypeId != target.DerivedFromCkTypeId)
        {
            differences.Add($"DerivedFromCkTypeId: '{source.DerivedFromCkTypeId}' vs '{target.DerivedFromCkTypeId}'");
        }

        if (source.AllAttributes.Count != target.AllAttributes.Count)
        {
            differences.Add($"Attributes count: {source.AllAttributes.Count} vs {target.AllAttributes.Count}");
        }

        int sourceAssocCount = source.Associations.In.All.Count + source.Associations.Out.All.Count;
        int targetAssocCount = target.Associations.In.All.Count + target.Associations.Out.All.Count;
        if (sourceAssocCount != targetAssocCount)
        {
            differences.Add($"Associations count: {sourceAssocCount} vs {targetAssocCount}");
        }

        if (source.Indexes.Count != target.Indexes.Count)
        {
            differences.Add($"Indexes count: {source.Indexes.Count} vs {target.Indexes.Count}");
        }

        return string.Join("; ", differences);
    }
}
