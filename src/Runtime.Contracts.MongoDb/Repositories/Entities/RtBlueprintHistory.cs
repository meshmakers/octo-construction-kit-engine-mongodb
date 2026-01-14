using System.Diagnostics;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

/// <summary>
/// Represents a blueprint application history entry stored in MongoDB.
/// Tracks when and how blueprints were applied to tenants.
/// </summary>
[DebuggerDisplay("TenantId={TenantId}, BlueprintId={BlueprintId}-{BlueprintVersion}")]
public class RtBlueprintHistory
{
    /// <summary>
    /// Unique identifier for this history entry
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// The tenant this blueprint was applied to
    /// </summary>
    public string TenantId { get; set; } = null!;

    /// <summary>
    /// The name of the applied blueprint (without version)
    /// </summary>
    public string BlueprintId { get; set; } = null!;

    /// <summary>
    /// The version of the applied blueprint
    /// </summary>
    public string BlueprintVersion { get; set; } = null!;

    /// <summary>
    /// When the blueprint was applied
    /// </summary>
    public DateTime AppliedAt { get; set; }

    /// <summary>
    /// How the blueprint was applied (Initial, Update, Migration)
    /// </summary>
    public string ApplicationMode { get; set; } = null!;

    /// <summary>
    /// The previous blueprint name (if this was an update)
    /// </summary>
    public string? PreviousBlueprintId { get; set; }

    /// <summary>
    /// The previous blueprint version (if this was an update)
    /// </summary>
    public string? PreviousVersion { get; set; }

    /// <summary>
    /// Number of entities created during blueprint application
    /// </summary>
    public int EntitiesCreated { get; set; }

    /// <summary>
    /// Number of entities updated during blueprint application
    /// </summary>
    public int EntitiesUpdated { get; set; }

    /// <summary>
    /// Number of entities deleted during blueprint application
    /// </summary>
    public int EntitiesDeleted { get; set; }

    /// <summary>
    /// Checksum of the seed data for change detection
    /// </summary>
    public string? SeedDataChecksum { get; set; }
}
