using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Comparator for CkType comparison between tenants
/// </summary>
internal class CkTypeComparator
{
    /// <summary>
    ///     Compares CkType lists from source and target tenants
    /// </summary>
    /// <param name="sourceTypes">Source tenant CkTypes</param>
    /// <param name="targetTypes">Target tenant CkTypes</param>
    /// <returns>CkType comparison results with identified differences</returns>
    public CkTypeComparison Compare(List<CkType> sourceTypes, List<CkType> targetTypes)
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
            bool inSource = sourceByTypeId.TryGetValue(typeId, out CkType? sourceType);
            bool inTarget = targetByTypeId.TryGetValue(typeId, out CkType? targetType);

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

    private bool AreTypesIdentical(CkType source, CkType target)
    {
        // Compare key properties
        if (source.CkModelId != target.CkModelId) return false;
        if (source.IsFinal != target.IsFinal) return false;
        if (source.IsAbstract != target.IsAbstract) return false;
        if (source.Description != target.Description) return false;
        if (source.IsCollectionRoot != target.IsCollectionRoot) return false;
        if (source.CollectionName != target.CollectionName) return false;
        if (source.EnableChangeStreamPreAndPostImages != target.EnableChangeStreamPreAndPostImages) return false;

        // Compare attribute count (simple comparison)
        if (source.Attributes.Count != target.Attributes.Count) return false;

        return true;
    }

    private string BuildDifferenceDescription(CkType source, CkType target)
    {
        var differences = new List<string>();

        if (source.CkModelId != target.CkModelId)
        {
            differences.Add($"CkModelId: '{source.CkModelId}' vs '{target.CkModelId}'");
        }

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

        if (source.CollectionName != target.CollectionName)
        {
            differences.Add($"CollectionName: '{source.CollectionName}' vs '{target.CollectionName}'");
        }

        if (source.EnableChangeStreamPreAndPostImages != target.EnableChangeStreamPreAndPostImages)
        {
            differences.Add($"EnableChangeStreamPreAndPostImages: {source.EnableChangeStreamPreAndPostImages} vs {target.EnableChangeStreamPreAndPostImages}");
        }

        if (source.Attributes.Count != target.Attributes.Count)
        {
            differences.Add($"Attributes count: {source.Attributes.Count} vs {target.Attributes.Count}");
        }

        return string.Join("; ", differences);
    }
}
