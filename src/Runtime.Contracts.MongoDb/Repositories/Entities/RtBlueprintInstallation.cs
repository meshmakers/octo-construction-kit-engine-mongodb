using System.Diagnostics;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

/// <summary>
/// Represents an active blueprint installation on a tenant. Unlike
/// <see cref="RtBlueprintHistory"/> (an append-only audit log), there is
/// exactly one row per (tenantId, blueprintName) pair, mutated on every
/// Apply / Update / ReApply.
/// </summary>
[DebuggerDisplay("TenantId={TenantId}, BlueprintId={BlueprintName}-{BlueprintVersion}")]
public class RtBlueprintInstallation
{
    /// <summary>
    /// Unique identifier for this installation row.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Tenant this blueprint is installed on.
    /// </summary>
    public string TenantId { get; set; } = null!;

    /// <summary>
    /// Blueprint name (without version).
    /// </summary>
    public string BlueprintName { get; set; } = null!;

    /// <summary>
    /// Currently-installed version of the blueprint.
    /// </summary>
    public string BlueprintVersion { get; set; } = null!;

    /// <summary>
    /// When the blueprint was first installed on the tenant.
    /// </summary>
    public DateTime InstalledAt { get; set; }

    /// <summary>
    /// When the installation row was last touched.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>
    /// Checksum of the seed data file applied, if any.
    /// </summary>
    public string? SeedDataChecksum { get; set; }

    /// <summary>
    /// Fully-qualified blueprint ids ("Name-Version") that were installed
    /// as transitive dependencies of this blueprint.
    /// </summary>
    public List<string> ResolvedDependencies { get; set; } = [];

    /// <summary>
    /// <c>true</c> when this row was created as a dependency of another
    /// blueprint rather than an explicit install.
    /// </summary>
    public bool IsDependency { get; set; }
}
