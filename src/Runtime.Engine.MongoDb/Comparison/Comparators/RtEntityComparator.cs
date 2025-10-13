using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Comparator for RtEntity comparison between tenants
/// </summary>
internal class RtEntityComparator
{
    /// <summary>
    ///     Compares RtEntity lists from source and target tenants grouped by CkTypeId
    /// </summary>
    /// <param name="sourceEntities">Source tenant entities grouped by CkTypeId</param>
    /// <param name="targetEntities">Target tenant entities grouped by CkTypeId</param>
    /// <param name="sourceCkTypes">Source tenant CkTypes for attribute metadata</param>
    /// <param name="targetCkTypes">Target tenant CkTypes for attribute metadata</param>
    /// <param name="options">Comparison options</param>
    /// <returns>Dictionary of RtEntityTypeComparison results by CkTypeId</returns>
    public Dictionary<string, RtEntityTypeComparison> Compare(
        Dictionary<string, List<RtEntity>> sourceEntities,
        Dictionary<string, List<RtEntity>> targetEntities,
        List<CkType> sourceCkTypes,
        List<CkType> targetCkTypes,
        TenantComparisonOptions options)
    {
        var results = new Dictionary<string, RtEntityTypeComparison>();

        // Create dictionaries for quick CkType lookup
        var sourceCkTypesDict = sourceCkTypes.ToDictionary(t => t.CkTypeId.ToString());
        var targetCkTypesDict = targetCkTypes.ToDictionary(t => t.CkTypeId.ToString());

        // Get all unique CkTypeIds from both tenants
        var allCkTypeIds = new HashSet<string>(sourceEntities.Keys);
        foreach (string ckTypeId in targetEntities.Keys)
        {
            allCkTypeIds.Add(ckTypeId);
        }

        // Compare entities for each CkTypeId
        foreach (string ckTypeId in allCkTypeIds)
        {
            bool hasSource = sourceEntities.TryGetValue(ckTypeId, out List<RtEntity>? sourceList);
            bool hasTarget = targetEntities.TryGetValue(ckTypeId, out List<RtEntity>? targetList);

            var comparison = new RtEntityTypeComparison
            {
                CkTypeId = ckTypeId,
                SourceTotalCount = hasSource ? sourceList!.Count : 0,
                TargetTotalCount = hasTarget ? targetList!.Count : 0,
                SourceFilteredCount = hasSource ? sourceList!.Count : 0,
                TargetFilteredCount = hasTarget ? targetList!.Count : 0
            };

            // Get CkType for attribute comparison
            sourceCkTypesDict.TryGetValue(ckTypeId, out CkType? sourceCkType);
            targetCkTypesDict.TryGetValue(ckTypeId, out CkType? targetCkType);

            if (hasSource && hasTarget)
            {
                CompareEntitiesForType(sourceList!, targetList!, comparison, sourceCkType, targetCkType, options);
            }
            else if (hasSource && !hasTarget)
            {
                // All source entities are only in source
                comparison.OnlyInSource.AddRange(sourceList!);
            }
            else if (!hasSource && hasTarget)
            {
                // All target entities are only in target
                comparison.OnlyInTarget.AddRange(targetList!);
            }

            results[ckTypeId] = comparison;
        }

        return results;
    }

    private void CompareEntitiesForType(
        List<RtEntity> sourceList,
        List<RtEntity> targetList,
        RtEntityTypeComparison comparison,
        CkType? sourceCkType,
        CkType? targetCkType,
        TenantComparisonOptions options)
    {
        // Create dictionaries for matching
        var targetByRtId = targetList.ToDictionary(e => e.RtId.ToString());
        var targetByWellKnownName = targetList
            .Where(e => !string.IsNullOrEmpty(e.RtWellKnownName))
            .GroupBy(e => e.RtWellKnownName!)
            .ToDictionary(g => g.Key, g => g.First());

        var matchedTargets = new HashSet<string>();

        foreach (RtEntity sourceEntity in sourceList)
        {
            RtEntity? matchedTarget = null;
            string matchedBy = string.Empty;

            // Strategy 1: Match by RtId
            if (targetByRtId.TryGetValue(sourceEntity.RtId.ToString(), out RtEntity? targetById))
            {
                matchedTarget = targetById;
                matchedBy = "ByCkTypeIdAndRtId";
            }
            // Strategy 2: Match by RtWellKnownName if present and not already matched
            else if (!string.IsNullOrEmpty(sourceEntity.RtWellKnownName) &&
                     targetByWellKnownName.TryGetValue(sourceEntity.RtWellKnownName, out RtEntity? targetByName) &&
                     !matchedTargets.Contains(targetByName.RtId.ToString()))
            {
                matchedTarget = targetByName;
                matchedBy = "ByRtWellKnownName";
            }

            if (matchedTarget != null)
            {
                // Mark as matched
                matchedTargets.Add(matchedTarget.RtId.ToString());

                // Compare the matched entities
                RtEntityDifference? difference = CompareEntities(
                    sourceEntity,
                    matchedTarget,
                    matchedBy,
                    sourceCkType,
                    targetCkType,
                    options);

                if (difference != null && difference.PropertyDifferences.Count > 0)
                {
                    comparison.Differences.Add(difference);
                }
                else
                {
                    comparison.MatchedIdenticalCount++;
                }
            }
            else
            {
                // Entity only in source
                comparison.OnlyInSource.Add(sourceEntity);
            }
        }

        // Find entities only in target
        foreach (RtEntity targetEntity in targetList)
        {
            if (!matchedTargets.Contains(targetEntity.RtId.ToString()))
            {
                comparison.OnlyInTarget.Add(targetEntity);
            }
        }
    }

    private RtEntityDifference? CompareEntities(
        RtEntity source,
        RtEntity target,
        string matchedBy,
        CkType? sourceCkType,
        CkType? targetCkType,
        TenantComparisonOptions options)
    {
        var propertyDifferences = new List<PropertyDifference>();

        // Compare system properties
        CompareSystemProperties(source, target, propertyDifferences);

        // Compare attributes if requested
        if (options.IncludePropertyDifferences && sourceCkType != null && targetCkType != null)
        {
            CompareAttributes(source, target, sourceCkType, targetCkType, propertyDifferences);
        }

        if (propertyDifferences.Count == 0)
        {
            return null; // No differences
        }

        return new RtEntityDifference
        {
            SourceEntity = source,
            TargetEntity = target,
            MatchedBy = matchedBy,
            PropertyDifferences = propertyDifferences
        };
    }

    private void CompareSystemProperties(
        RtEntity source,
        RtEntity target,
        List<PropertyDifference> differences)
    {
        // Compare RtWellKnownName
        if (source.RtWellKnownName != target.RtWellKnownName)
        {
            differences.Add(new PropertyDifference
            {
                PropertyName = "RtWellKnownName",
                DifferenceType = DifferenceType.Modified,
                SourceValue = source.RtWellKnownName,
                TargetValue = target.RtWellKnownName
            });
        }

        // Compare RtCreationDateTime
        if (source.RtCreationDateTime != target.RtCreationDateTime)
        {
            differences.Add(new PropertyDifference
            {
                PropertyName = "RtCreationDateTime",
                DifferenceType = DifferenceType.Modified,
                SourceValue = source.RtCreationDateTime,
                TargetValue = target.RtCreationDateTime
            });
        }

        // Compare RtChangedDateTime
        if (source.RtChangedDateTime != target.RtChangedDateTime)
        {
            differences.Add(new PropertyDifference
            {
                PropertyName = "RtChangedDateTime",
                DifferenceType = DifferenceType.Modified,
                SourceValue = source.RtChangedDateTime,
                TargetValue = target.RtChangedDateTime
            });
        }
    }

    private void CompareAttributes(
        RtEntity source,
        RtEntity target,
        CkType sourceCkType,
        CkType targetCkType,
        List<PropertyDifference> differences)
    {
        // Get all attribute names from both CkTypes
        var allAttributeNames = new HashSet<string>(
            sourceCkType.Attributes.Select(a => a.AttributeName));

        foreach (var attr in targetCkType.Attributes)
        {
            allAttributeNames.Add(attr.AttributeName);
        }

        // Compare Attributes (from RtEntity.Attributes dictionary)
        foreach (string attrName in allAttributeNames)
        {
            bool sourceHas = source.Attributes.TryGetValue(attrName, out object? sourceValue);
            bool targetHas = target.Attributes.TryGetValue(attrName, out object? targetValue);

            if (sourceHas && !targetHas)
            {
                differences.Add(new PropertyDifference
                {
                    PropertyName = attrName,
                    DifferenceType = DifferenceType.Removed,
                    SourceValue = sourceValue,
                    TargetValue = null
                });
            }
            else if (!sourceHas && targetHas)
            {
                differences.Add(new PropertyDifference
                {
                    PropertyName = attrName,
                    DifferenceType = DifferenceType.Added,
                    SourceValue = null,
                    TargetValue = targetValue
                });
            }
            else if (sourceHas && targetHas)
            {
                // Compare values
                if (!AreValuesEqual(sourceValue, targetValue))
                {
                    differences.Add(new PropertyDifference
                    {
                        PropertyName = attrName,
                        DifferenceType = DifferenceType.Modified,
                        SourceValue = sourceValue,
                        TargetValue = targetValue
                    });
                }
            }
        }
    }

    private bool AreValuesEqual(object? value1, object? value2)
    {
        if (value1 == null && value2 == null) return true;
        if (value1 == null || value2 == null) return false;

        return value1.Equals(value2);
    }
}
