using System.Diagnostics;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

internal class TenantComparisonService : ITenantComparisonService
{
    private readonly MetadataLoader _metadataLoader;
    private readonly MetadataComparator _metadataComparator;

    public TenantComparisonService(MetadataLoader metadataLoader, MetadataComparator metadataComparator)
    {
        _metadataLoader = metadataLoader;
        _metadataComparator = metadataComparator;
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

        // Calculate total differences
        report.Summary.TotalDifferences =
            report.Summary.MetadataDifferences +
            report.Summary.CkModelDifferences +
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
