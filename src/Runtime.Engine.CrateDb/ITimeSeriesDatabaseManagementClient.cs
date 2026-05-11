namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Provides management operations to a stream data database. After T17 the legacy single-tenant
/// table is gone — the only DDL flowing through here is per-archive table creation/drop driven by
/// the archive lifecycle service.
/// </summary>
public interface IStreamDataDatabaseManagementClient
{
    /// <summary>
    /// Drops every CrateDB table in the tenant's schema. Used when a tenant is deleted; CrateDB
    /// has no <c>DROP SCHEMA</c> so we fan-out to every table the project owns. Idempotent.
    /// </summary>
    Task DeleteStreamDataDatabaseAsync(string tenantId);

    /// <summary>
    /// Returns the table names present in the tenant's CrateDB schema, queried via
    /// <c>information_schema.tables</c>. Used by the startup reconciliation job (concept §11) to
    /// detect orphan tables and Activated archives that lost their backing storage.
    /// </summary>
    Task<IReadOnlyList<string>> ListTablesInTenantSchemaAsync(string tenantId);

    /// <summary>
    /// Executes a single DDL statement against the tenant's CrateDB connection. Used by the
    /// archive lifecycle service to provision/drop per-archive tables built via
    /// <see cref="ArchiveDdlGenerator"/>.
    /// </summary>
    Task ExecuteDdlAsync(string tenantId, string sql);
}