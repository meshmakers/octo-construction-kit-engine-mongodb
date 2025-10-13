using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;

/// <summary>
///     Compares tenant metadata between two tenants
/// </summary>
internal class MetadataComparator
{
    /// <summary>
    ///     Compares two tenant metadata objects and identifies differences
    /// </summary>
    /// <param name="source">Source tenant metadata</param>
    /// <param name="target">Target tenant metadata</param>
    /// <returns>Metadata comparison results with identified differences</returns>
    public MetadataComparison Compare(TenantMetadata source, TenantMetadata target)
    {
        MetadataComparison comparison = new()
        {
            Source = source,
            Target = target,
            Differences = new List<MetadataDifference>()
        };

        // Compare database names
        if (source.DatabaseName != target.DatabaseName)
        {
            comparison.Differences.Add(new MetadataDifference
            {
                FieldName = nameof(TenantMetadata.DatabaseName),
                SourceValue = source.DatabaseName,
                TargetValue = target.DatabaseName,
                Description = $"Database name differs: '{source.DatabaseName}' vs '{target.DatabaseName}'"
            });
        }

        // Compare total RtEntity counts
        if (source.TotalRtEntityCount != target.TotalRtEntityCount)
        {
            comparison.Differences.Add(new MetadataDifference
            {
                FieldName = nameof(TenantMetadata.TotalRtEntityCount),
                SourceValue = source.TotalRtEntityCount,
                TargetValue = target.TotalRtEntityCount,
                Description = $"Total RtEntity count differs: {source.TotalRtEntityCount} vs {target.TotalRtEntityCount}"
            });
        }

        // Compare CkModel counts
        if (source.CkModelCount != target.CkModelCount)
        {
            comparison.Differences.Add(new MetadataDifference
            {
                FieldName = nameof(TenantMetadata.CkModelCount),
                SourceValue = source.CkModelCount,
                TargetValue = target.CkModelCount,
                Description = $"CkModel count differs: {source.CkModelCount} vs {target.CkModelCount}"
            });
        }

        // Compare total association counts
        if (source.TotalAssociationCount != target.TotalAssociationCount)
        {
            comparison.Differences.Add(new MetadataDifference
            {
                FieldName = nameof(TenantMetadata.TotalAssociationCount),
                SourceValue = source.TotalAssociationCount,
                TargetValue = target.TotalAssociationCount,
                Description = $"Total association count differs: {source.TotalAssociationCount} vs {target.TotalAssociationCount}"
            });
        }

        // Compare RtEntity counts by CkType
        CompareRtEntityCountsByCkType(source, target, comparison);

        return comparison;
    }

    private void CompareRtEntityCountsByCkType(TenantMetadata source, TenantMetadata target, MetadataComparison comparison)
    {
        HashSet<string> allCkTypes = new HashSet<string>(source.RtEntityCountByCkType.Keys);
        foreach (string ckTypeId in target.RtEntityCountByCkType.Keys)
        {
            allCkTypes.Add(ckTypeId);
        }

        foreach (string ckTypeId in allCkTypes)
        {
            bool inSource = source.RtEntityCountByCkType.TryGetValue(ckTypeId, out long sourceCount);
            bool inTarget = target.RtEntityCountByCkType.TryGetValue(ckTypeId, out long targetCount);

            if (!inSource && inTarget)
            {
                comparison.Differences.Add(new MetadataDifference
                {
                    FieldName = $"{nameof(TenantMetadata.RtEntityCountByCkType)}[{ckTypeId}]",
                    SourceValue = 0,
                    TargetValue = targetCount,
                    Description = $"CkType '{ckTypeId}' exists only in target with {targetCount} entities"
                });
            }
            else if (inSource && !inTarget)
            {
                comparison.Differences.Add(new MetadataDifference
                {
                    FieldName = $"{nameof(TenantMetadata.RtEntityCountByCkType)}[{ckTypeId}]",
                    SourceValue = sourceCount,
                    TargetValue = 0,
                    Description = $"CkType '{ckTypeId}' exists only in source with {sourceCount} entities"
                });
            }
            else if (sourceCount != targetCount)
            {
                comparison.Differences.Add(new MetadataDifference
                {
                    FieldName = $"{nameof(TenantMetadata.RtEntityCountByCkType)}[{ckTypeId}]",
                    SourceValue = sourceCount,
                    TargetValue = targetCount,
                    Description = $"CkType '{ckTypeId}' entity count differs: {sourceCount} vs {targetCount}"
                });
            }
        }
    }
}
