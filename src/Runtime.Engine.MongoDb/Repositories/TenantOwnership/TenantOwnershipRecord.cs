namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     The single ownership marker document of a tenant database (AB#4945). Lives in the TENANT
///     database itself (non-CK collection <c>tenant_ownership</c>, one document with a fixed id),
///     so it travels with the database: any OctoMesh instance that can physically reach the
///     database can read who owns it — which is exactly what the cross-instance attach guard
///     needs, since a second instance cannot see the first instance's system registry.
/// </summary>
internal sealed class TenantOwnershipRecord
{
    /// <summary>Fixed document id — the collection holds exactly one marker.</summary>
    public const string DocumentId = "owner";

    public string Id { get; set; } = DocumentId;

    /// <summary>
    ///     The owning instance, identified by its normalized system database name — the one value
    ///     that is unique per OctoMesh instance on a shared MongoDB server (the instance separator
    ///     per Epic AB#4944). Compared ordinally against the local instance's normalized
    ///     <c>SystemDatabaseName</c>.
    /// </summary>
    public string OwnerSystemDatabaseName { get; set; } = string.Empty;

    /// <summary>Tenant id the database was registered under when the marker was written.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>When the owning instance stamped the marker (create, attach, or lazy claim).</summary>
    public DateTime AttachedAtUtc { get; set; }
}
