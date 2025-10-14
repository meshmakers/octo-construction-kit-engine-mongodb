using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

/// <summary>
///     Main service for comparing two tenants in the OctoMesh system
/// </summary>
public interface ITenantComparisonService
{
    /// <summary>
    ///     Compares two tenants and generates a comprehensive comparison report
    /// </summary>
    /// <param name="sourceTenantId">Source tenant identifier</param>
    /// <param name="targetTenantId">Target tenant identifier</param>
    /// <param name="options">Comparison options and configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Hierarchical comparison report containing all requested comparison areas</returns>
    Task<TenantComparisonReport> CompareTenantAsync(
        string sourceTenantId,
        string targetTenantId,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Compares a live tenant with a backup archive.
    /// </summary>
    /// <param name="liveTenantId">The ID of the live tenant (source)</param>
    /// <param name="backupArchivePath">File system path to the backup archive (.gz file)</param>
    /// <param name="options">Comparison configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A comparison report detailing the differences</returns>
    /// <exception cref="ArgumentNullException">If any required parameter is null</exception>
    /// <exception cref="FileNotFoundException">If the backup archive file does not exist</exception>
    /// <exception cref="InvalidOperationException">If the restore or comparison fails</exception>
    Task<TenantComparisonReport> CompareTenantWithBackupAsync(
        string liveTenantId,
        string backupArchivePath,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Compares two backup archives.
    /// </summary>
    /// <param name="sourceBackupPath">File system path to the source backup archive</param>
    /// <param name="targetBackupPath">File system path to the target backup archive</param>
    /// <param name="options">Comparison configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A comparison report detailing the differences</returns>
    /// <exception cref="ArgumentNullException">If any required parameter is null</exception>
    /// <exception cref="FileNotFoundException">If either backup archive file does not exist</exception>
    /// <exception cref="InvalidOperationException">If restore or comparison fails</exception>
    Task<TenantComparisonReport> CompareBackupsAsync(
        string sourceBackupPath,
        string targetBackupPath,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Compares a backup archive with a live tenant (reverse comparison).
    /// </summary>
    /// <param name="backupArchivePath">File system path to the backup archive (.gz file)</param>
    /// <param name="liveTenantId">The ID of the live tenant (target)</param>
    /// <param name="options">Comparison configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A comparison report detailing the differences</returns>
    /// <exception cref="ArgumentNullException">If any required parameter is null</exception>
    /// <exception cref="FileNotFoundException">If the backup archive file does not exist</exception>
    /// <exception cref="InvalidOperationException">If the restore or comparison fails</exception>
    Task<TenantComparisonReport> CompareBackupWithTenantAsync(
        string backupArchivePath,
        string liveTenantId,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default);
}
