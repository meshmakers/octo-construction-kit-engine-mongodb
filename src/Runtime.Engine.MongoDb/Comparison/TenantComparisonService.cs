using System.Diagnostics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

internal class TenantComparisonService : ITenantComparisonService
{
    private readonly ISystemContext _systemContext;
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

    public TenantComparisonService(ISystemContext systemContext,
        MetadataLoader metadataLoader, MetadataComparator metadataComparator,
        CkModelLoader ckModelLoader, CkModelComparator ckModelComparator,
        CkTypeLoader ckTypeLoader, CkTypeComparator ckTypeComparator,
        RtEntityLoader rtEntityLoader, RtEntityComparator rtEntityComparator,
        AssociationLoader associationLoader, AssociationComparator associationComparator)
    {
        _systemContext = systemContext;
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

    public async Task<TenantComparisonReport> CompareTenantWithBackupAsync(string liveTenantId, string backupArchivePath,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        // Validate inputs
        if (string.IsNullOrEmpty(liveTenantId))
        {
            throw new ArgumentException("Live tenant ID cannot be null or empty", nameof(liveTenantId));
        }

        ValidateBackupPath(backupArchivePath, nameof(backupArchivePath));

        // Generate temporary tenant information
        (string tempTenantId, string tempDatabaseName) = GenerateTemporaryTenantInfo();

        try
        {
            // Restore backup to temporary tenant
            await RestoreBackupToTemporaryTenantAsync(tempTenantId, tempDatabaseName, backupArchivePath, cancellationToken);

            // Perform comparison between live tenant and restored backup
            TenantComparisonReport report = await CompareTenantAsync(
                sourceTenantId: liveTenantId,
                targetTenantId: tempTenantId,
                options: options,
                cancellationToken: cancellationToken);

            // Update metadata to indicate target was restored from backup
            report.Metadata.TargetWasRestoredFromBackup = true;
            report.Metadata.TargetBackupPath = backupArchivePath;

            return report;
        }
        finally
        {
            // Cleanup: Drop the temporary tenant in any case
            await CleanupTemporaryTenantAsync(tempTenantId);
        }
    }

    public async Task<TenantComparisonReport> CompareBackupsAsync(string sourceBackupPath, string targetBackupPath,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        // Validate inputs
        ValidateBackupPath(sourceBackupPath, nameof(sourceBackupPath));
        ValidateBackupPath(targetBackupPath, nameof(targetBackupPath));

        // Generate temporary tenant information with descriptive suffixes
        (string sourceTempTenantId, string sourceTempDatabaseName) = GenerateTemporaryTenantInfo("source");
        (string targetTempTenantId, string targetTempDatabaseName) = GenerateTemporaryTenantInfo("target");

        try
        {
            // Restore both backups to temporary tenants
            await RestoreBackupToTemporaryTenantAsync(sourceTempTenantId, sourceTempDatabaseName, sourceBackupPath, cancellationToken);
            await RestoreBackupToTemporaryTenantAsync(targetTempTenantId, targetTempDatabaseName, targetBackupPath, cancellationToken);

            // Perform comparison between both restored backups
            TenantComparisonReport report = await CompareTenantAsync(
                sourceTenantId: sourceTempTenantId,
                targetTenantId: targetTempTenantId,
                options: options,
                cancellationToken: cancellationToken);

            // Update metadata to indicate both were restored from backups
            report.Metadata.SourceWasRestoredFromBackup = true;
            report.Metadata.SourceBackupPath = sourceBackupPath;
            report.Metadata.TargetWasRestoredFromBackup = true;
            report.Metadata.TargetBackupPath = targetBackupPath;

            return report;
        }
        finally
        {
            // Cleanup: Drop both temporary tenants in any case
            await CleanupTemporaryTenantAsync(sourceTempTenantId);
            await CleanupTemporaryTenantAsync(targetTempTenantId);
        }
    }

    /// <summary>
    ///     Generates a unique temporary tenant ID and database name
    ///     Uses short names to respect MongoDB's 64-character database name limit (38 chars on some Windows systems)
    /// </summary>
    /// <param name="suffix">Optional suffix for the tenant ID (e.g., "source", "target")</param>
    /// <returns>A tuple containing the tenant ID and database name</returns>
    private static (string TenantId, string DatabaseName) GenerateTemporaryTenantInfo(string? suffix = null)
    {
        // Use only first 12 characters of GUID for uniqueness (sufficient for temporary operations)
        string shortGuid = Guid.NewGuid().ToString("N")[..12];

        // Use shortened suffix (first 3 chars: "src", "tgt")
        string shortSuffix = string.IsNullOrEmpty(suffix)
            ? string.Empty
            : $"_{suffix[..Math.Min(3, suffix.Length)]}";

        // Format: "tcmp_{suffix}_{shortGuid}" (e.g., "tcmp_src_a1b2c3d4e5f6")
        // Maximum length: 4 + 1 + 3 + 1 + 12 = 21 characters (well within MongoDB limits)
        string tenantId = $"tcmp{shortSuffix}_{shortGuid}";
        string databaseName = tenantId; // Use same format for consistency

        return (tenantId, databaseName);
    }

    /// <summary>
    ///     Validates that a backup archive path exists
    /// </summary>
    /// <param name="backupPath">The backup archive file path to validate</param>
    /// <param name="parameterName">The parameter name for exception messages</param>
    /// <exception cref="ArgumentException">Thrown when the path is null or empty</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist</exception>
    private static void ValidateBackupPath(string backupPath, string parameterName)
    {
        if (string.IsNullOrEmpty(backupPath))
        {
            throw new ArgumentException($"Backup archive path cannot be null or empty", parameterName);
        }

        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"Backup archive not found: {backupPath}", backupPath);
        }
    }

    /// <summary>
    ///     Restores a backup archive to a temporary tenant
    /// </summary>
    /// <param name="tenantId">The tenant ID to use for the restored tenant</param>
    /// <param name="databaseName">The database name to restore to</param>
    /// <param name="backupPath">Path to the backup archive file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="InvalidOperationException">Thrown when restore operation fails</exception>
    private async Task RestoreBackupToTemporaryTenantAsync(string tenantId, string databaseName,
        string backupPath, CancellationToken cancellationToken)
    {
        CommandResult restoreResult = await _systemContext.RestoreTenantAsync(
            tenantId: tenantId,
            databaseName: databaseName,
            archiveFilePath: backupPath,
            sourceDatabaseName: null,
            dropExistingTenant: true,
            attachTenant: true,
            timeout: null,
            cancellationToken: cancellationToken);

        if (!restoreResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to restore backup '{backupPath}' to temporary tenant '{tenantId}': {restoreResult.Error}");
        }
    }

    /// <summary>
    ///     Helper method to cleanup a temporary tenant, suppressing any errors
    /// </summary>
    private async Task CleanupTemporaryTenantAsync(string tenantId)
    {
        try
        {
            ITenantContext? tempTenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
            if (tempTenantContext != null)
            {
                using IOctoAdminSession session = await _systemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await _systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
        }
        catch
        {
            // Suppress cleanup errors to avoid masking the original exception
        }
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
