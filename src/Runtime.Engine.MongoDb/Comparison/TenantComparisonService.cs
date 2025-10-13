using System.Diagnostics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

internal class TenantComparisonService : ITenantComparisonService
{
    private readonly MetadataLoader _metadataLoader;
    private readonly MetadataComparator _metadataComparator;
    private readonly CkModelLoader _ckModelLoader;
    private readonly CkModelComparator _ckModelComparator;
    private readonly CkTypeLoader _ckTypeLoader;
    private readonly CkTypeComparator _ckTypeComparator;
    private readonly RtEntityLoader _rtEntityLoader;
    private readonly RtEntityComparator _rtEntityComparator;
    private readonly AssociationLoader _associationLoader;
    private readonly AssociationComparator _associationComparator;

    public TenantComparisonService(MetadataLoader metadataLoader, MetadataComparator metadataComparator,
        CkModelLoader ckModelLoader, CkModelComparator ckModelComparator,
        CkTypeLoader ckTypeLoader, CkTypeComparator ckTypeComparator,
        RtEntityLoader rtEntityLoader, RtEntityComparator rtEntityComparator,
        AssociationLoader associationLoader, AssociationComparator associationComparator)
    {
        _metadataLoader = metadataLoader;
        _metadataComparator = metadataComparator;
        _ckModelLoader = ckModelLoader;
        _ckModelComparator = ckModelComparator;
        _ckTypeLoader = ckTypeLoader;
        _ckTypeComparator = ckTypeComparator;
        _rtEntityLoader = rtEntityLoader;
        _rtEntityComparator = rtEntityComparator;
        _associationLoader = associationLoader;
        _associationComparator = associationComparator;
    }

    public async Task<TenantComparisonReport> CompareTenantAsync(string sourceTenantId, string targetTenantId,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var comparisonDate = DateTimeOffset.UtcNow;

        // Initialize report
        var report = new TenantComparisonReport
        {
            Metadata = new ComparisonMetadata
            {
                ComparisonDate = comparisonDate,
                SourceTenantId = sourceTenantId,
                TargetTenantId = targetTenantId,
                OptionsDescription = BuildOptionsDescription(options),
                SourceWasRestoredFromBackup = false,
                TargetWasRestoredFromBackup = false,
            },
            Summary = new ComparisonSummary(),
        };

        // Perform metadata comparison if requested
        if (options.Areas.HasFlag(ComparisonAreas.Metadata))
        {
            var sourceMetadata = await _metadataLoader.LoadAsync(sourceTenantId, options, cancellationToken);
            var targetMetadata = await _metadataLoader.LoadAsync(targetTenantId, options, cancellationToken);

            var metadataComparison = _metadataComparator.Compare(sourceMetadata, targetMetadata);

            report.MetadataComparison = metadataComparison;
            report.Summary.MetadataDifferences = metadataComparison.Differences.Count;
        }

        // Perform CkModel comparison if requested
        if (options.Areas.HasFlag(ComparisonAreas.CkModels))
        {
            var sourceCkModels = await _ckModelLoader.LoadAsync(sourceTenantId, options, cancellationToken);
            var targetCkModels = await _ckModelLoader.LoadAsync(targetTenantId, options, cancellationToken);

            var ckModelComparison = _ckModelComparator.Compare(sourceCkModels, targetCkModels);

            report.CkModelComparison = ckModelComparison;
            report.Summary.CkModelDifferences = ckModelComparison.TotalDifferences;
        }

        // Perform CkType comparison if requested
        List<CkType> sourceCkTypes = new();
        List<CkType> targetCkTypes = new();

        if (options.Areas.HasFlag(ComparisonAreas.CkTypes))
        {
            sourceCkTypes = await _ckTypeLoader.LoadAsync(sourceTenantId, options, cancellationToken);
            targetCkTypes = await _ckTypeLoader.LoadAsync(targetTenantId, options, cancellationToken);

            var ckTypeComparison = _ckTypeComparator.Compare(sourceCkTypes, targetCkTypes);

            report.CkTypeComparison = ckTypeComparison;
            report.Summary.CkTypeDifferences = ckTypeComparison.TotalDifferences;
        }

        // Perform RtEntity comparison if requested
        if (options.Areas.HasFlag(ComparisonAreas.RtEntities))
        {
            // Load CkTypes if not already loaded (needed for entity comparison)
            if (sourceCkTypes.Count == 0)
            {
                sourceCkTypes = await _ckTypeLoader.LoadAsync(sourceTenantId, options, cancellationToken);
            }
            if (targetCkTypes.Count == 0)
            {
                targetCkTypes = await _ckTypeLoader.LoadAsync(targetTenantId, options, cancellationToken);
            }

            var sourceEntities = await _rtEntityLoader.LoadAsync(sourceTenantId, options, cancellationToken);
            var targetEntities = await _rtEntityLoader.LoadAsync(targetTenantId, options, cancellationToken);

            var rtEntityComparisons = _rtEntityComparator.Compare(
                sourceEntities, targetEntities,
                sourceCkTypes, targetCkTypes,
                options);

            report.RtEntityComparisons = rtEntityComparisons;
            report.Summary.RtEntityDifferences = rtEntityComparisons.Values.Sum(c => c.TotalDifferences);
        }

        // Perform Association comparison if requested
        if (options.Areas.HasFlag(ComparisonAreas.Associations))
        {
            var sourceAssociations = await _associationLoader.LoadAsync(sourceTenantId, options, cancellationToken);
            var targetAssociations = await _associationLoader.LoadAsync(targetTenantId, options, cancellationToken);

            var associationComparison = _associationComparator.Compare(sourceAssociations, targetAssociations);

            report.AssociationComparison = associationComparison;
            report.Summary.AssociationDifferences = associationComparison.TotalDifferences;
        }

        // Calculate total differences
        report.Summary.TotalDifferences =
            report.Summary.MetadataDifferences +
            report.Summary.CkModelDifferences +
            report.Summary.CkTypeDifferences +
            report.Summary.RtEntityDifferences +
            report.Summary.AssociationDifferences;

        stopwatch.Stop();
        report.Metadata.Duration = stopwatch.Elapsed;

        return report;
    }

    public Task<TenantComparisonReport> CompareTenantWithBackupAsync(string liveTenantId, string backupArchivePath,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TenantComparisonReport());
    }

    public Task<TenantComparisonReport> CompareBackupsAsync(string sourceBackupPath, string targetBackupPath,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TenantComparisonReport());
    }

    private string BuildOptionsDescription(TenantComparisonOptions options)
    {
        var parts = new List<string>();

        if (options.Areas.HasFlag(ComparisonAreas.Metadata))
        {
            parts.Add("Metadata");
        }

        if (options.Areas.HasFlag(ComparisonAreas.CkModels))
        {
            parts.Add("CkModels");
        }

        if (options.Areas.HasFlag(ComparisonAreas.CkTypes))
        {
            parts.Add("CkTypes");
        }

        if (options.Areas.HasFlag(ComparisonAreas.RtEntities))
        {
            parts.Add("RtEntities");
        }

        if (options.Areas.HasFlag(ComparisonAreas.Associations))
        {
            parts.Add("Associations");
        }

        var areasDescription = parts.Count > 0 ? $"Areas: {string.Join(", ", parts)}" : "Areas: None";

        if (options.MaxEntitiesPerType.HasValue)
        {
            areasDescription += $"; MaxEntitiesPerType: {options.MaxEntitiesPerType.Value}";
        }

        return areasDescription;
    }
}
