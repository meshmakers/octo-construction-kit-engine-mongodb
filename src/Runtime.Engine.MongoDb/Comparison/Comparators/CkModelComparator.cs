using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Compares CkModel collections between two tenants
/// </summary>
internal class CkModelComparator
{
    /// <summary>
    ///     Compares CkModelId collections from source and target tenants
    /// </summary>
    /// <param name="sourceModels">Source tenant CkModelIds</param>
    /// <param name="targetModels">Target tenant CkModelIds</param>
    /// <returns>CkModel comparison results with identified differences</returns>
    public CkModelComparison Compare(ICollection<CkModelId> sourceModels, ICollection<CkModelId> targetModels)
    {
        var comparison = new CkModelComparison();

        // Group models by ModelId (without version)
        var sourceByModelKey = sourceModels.GroupBy(m => m.ModelId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var targetByModelKey = targetModels.GroupBy(m => m.ModelId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Get all unique model keys
        var allModelKeys = new HashSet<string>(sourceByModelKey.Keys);
        foreach (string modelKey in targetByModelKey.Keys)
        {
            allModelKeys.Add(modelKey);
        }

        // Compare each model
        foreach (string modelKey in allModelKeys)
        {
            bool inSource = sourceByModelKey.TryGetValue(modelKey, out List<CkModelId>? sourceVersions);
            bool inTarget = targetByModelKey.TryGetValue(modelKey, out List<CkModelId>? targetVersions);

            if (inSource && !inTarget)
            {
                // Model exists only in source
                comparison.OnlyInSource.AddRange(sourceVersions!);
            }
            else if (!inSource && inTarget)
            {
                // Model exists only in target
                comparison.OnlyInTarget.AddRange(targetVersions!);
            }
            else if (inSource && inTarget)
            {
                // Model exists in both - compare versions
                CompareModelVersions(modelKey, sourceVersions!, targetVersions!, comparison);
            }
        }

        return comparison;
    }

    private void CompareModelVersions(string modelKey, List<CkModelId> sourceVersions,
        List<CkModelId> targetVersions, CkModelComparison comparison)
    {
        // Create dictionaries by full CkModelId (with version) for quick lookup
        var sourceByFullId = sourceVersions.ToDictionary(m => m.ToString());
        var targetByFullId = targetVersions.ToDictionary(m => m.ToString());

        // Get all unique full model IDs (with version)
        var allFullIds = new HashSet<string>(sourceByFullId.Keys);
        foreach (string fullId in targetByFullId.Keys)
        {
            allFullIds.Add(fullId);
        }

        foreach (string fullId in allFullIds)
        {
            bool inSource = sourceByFullId.TryGetValue(fullId, out CkModelId? sourceModel);
            bool inTarget = targetByFullId.TryGetValue(fullId, out CkModelId? targetModel);

            if (inSource && inTarget)
            {
                // Same version exists in both
                comparison.InBothSameVersion.Add(sourceModel!);
            }
            else if (inSource && !inTarget)
            {
                // Different version - source has a version that target doesn't
                // Check if target has any version of this model
                if (targetVersions.Count > 0)
                {
                    // Find the corresponding target version for this model
                    CkModelId targetVersion = targetVersions.First();
                    comparison.VersionDifferences.Add(new CkModelVersionDifference
                    {
                        ModelKey = modelKey,
                        SourceVersion = sourceModel!,
                        TargetVersion = targetVersion
                    });
                }
            }
            else if (!inSource && inTarget)
            {
                // Different version - target has a version that source doesn't
                // Check if this difference wasn't already recorded
                bool alreadyRecorded = comparison.VersionDifferences.Any(d => d.ModelKey == modelKey);
                if (!alreadyRecorded && sourceVersions.Count > 0)
                {
                    var sourceVersion = sourceVersions.First();
                    comparison.VersionDifferences.Add(new CkModelVersionDifference
                    {
                        ModelKey = modelKey,
                        SourceVersion = sourceVersion,
                        TargetVersion = targetModel!
                    });
                }
            }
        }
    }
}
