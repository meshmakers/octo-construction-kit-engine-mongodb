using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Compares CkModel collections between two tenants
/// </summary>
internal class CkModelComparator
{
    /// <summary>
    ///     Compares CkModel lists from source and target tenants
    /// </summary>
    /// <param name="sourceModels">Source tenant CkModels</param>
    /// <param name="targetModels">Target tenant CkModels</param>
    /// <returns>CkModel comparison results with identified differences</returns>
    public CkModelComparison Compare(List<CkModel> sourceModels, List<CkModel> targetModels)
    {
        var comparison = new CkModelComparison();

        // Group models by ModelId (without version)
        var sourceByModelId = sourceModels.GroupBy(m => m.ModelId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var targetByModelId = targetModels.GroupBy(m => m.ModelId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Get all unique ModelIds
        var allModelIds = new HashSet<string>(sourceByModelId.Keys);
        foreach (string modelId in targetByModelId.Keys)
        {
            allModelIds.Add(modelId);
        }

        // Compare each ModelId
        foreach (string modelId in allModelIds)
        {
            bool inSource = sourceByModelId.TryGetValue(modelId, out List<CkModel>? sourceVersions);
            bool inTarget = targetByModelId.TryGetValue(modelId, out List<CkModel>? targetVersions);

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
                CompareModelVersions(modelId, sourceVersions!, targetVersions!, comparison);
            }
        }

        return comparison;
    }

    private void CompareModelVersions(string modelId, List<CkModel> sourceVersions,
        List<CkModel> targetVersions, CkModelComparison comparison)
    {
        // Create dictionaries by full CkModelId for quick lookup
        var sourceByFullId = sourceVersions.ToDictionary(m => m.Id.ToString());
        var targetByFullId = targetVersions.ToDictionary(m => m.Id.ToString());

        // Get all unique full model IDs (with version)
        var allFullIds = new HashSet<string>(sourceByFullId.Keys);
        foreach (string fullId in targetByFullId.Keys)
        {
            allFullIds.Add(fullId);
        }

        foreach (string fullId in allFullIds)
        {
            bool inSource = sourceByFullId.TryGetValue(fullId, out CkModel? sourceModel);
            bool inTarget = targetByFullId.TryGetValue(fullId, out CkModel? targetModel);

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
                    var targetVersion = targetVersions.First();
                    comparison.VersionDifferences.Add(new CkModelVersionDifference
                    {
                        ModelId = modelId,
                        SourceVersion = sourceModel!,
                        TargetVersion = targetVersion
                    });
                }
            }
            else if (!inSource && inTarget)
            {
                // Different version - target has a version that source doesn't
                // Check if this difference wasn't already recorded
                bool alreadyRecorded = comparison.VersionDifferences.Any(d => d.ModelId == modelId);
                if (!alreadyRecorded && sourceVersions.Count > 0)
                {
                    var sourceVersion = sourceVersions.First();
                    comparison.VersionDifferences.Add(new CkModelVersionDifference
                    {
                        ModelId = modelId,
                        SourceVersion = sourceVersion,
                        TargetVersion = targetModel!
                    });
                }
            }
        }
    }
}
