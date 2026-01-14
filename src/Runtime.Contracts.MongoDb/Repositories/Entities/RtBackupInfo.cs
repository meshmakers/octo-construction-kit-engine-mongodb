using System.Diagnostics;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

/// <summary>
/// Represents backup metadata stored in MongoDB.
/// Tracks backup operations for tenants, especially for blueprint updates.
/// </summary>
[DebuggerDisplay("BackupId={BackupId}, TenantId={TenantId}")]
public class RtBackupInfo
{
    /// <summary>
    /// Unique identifier for this backup info entry (MongoDB document ID)
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Unique identifier for the backup itself
    /// </summary>
    public string BackupId { get; set; } = null!;

    /// <summary>
    /// The tenant that was backed up
    /// </summary>
    public string TenantId { get; set; } = null!;

    /// <summary>
    /// When the backup was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Reason for creating the backup (e.g., "Before update to MyBlueprint-2.0.0")
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Blueprint version at time of backup (if applicable)
    /// </summary>
    public string? BlueprintVersion { get; set; }

    /// <summary>
    /// Path or location where backup is stored
    /// </summary>
    public string? StorageLocation { get; set; }

    /// <summary>
    /// Size of the backup in bytes (if known)
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// Number of entities in the backup (if known)
    /// </summary>
    public int? EntityCount { get; set; }

    /// <summary>
    /// Type of backup (Full, Incremental, BlueprintUpdate)
    /// </summary>
    public string BackupType { get; set; } = "Full";
}
