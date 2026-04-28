namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Provides management operations to a stream data database
/// </summary>
public interface IStreamDataDatabaseManagementClient
{
    /// <summary>
    /// Creates a table in a stream data database for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task CreateStreamDataTableIfNotExistAsync(string tenantId);

    /// <summary>
    /// Deletes a table in a stream data database for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task DeleteStreamDataDatabaseAsync(string tenantId);

    /// <summary>
    /// Returns the table names present in the tenant's CrateDB schema, queried via
    /// <c>information_schema.tables</c>. Used by the startup reconciliation job (concept §11) to
    /// detect orphan tables and Activated archives that lost their backing storage.
    /// </summary>
    Task<IReadOnlyList<string>> ListTablesInTenantSchemaAsync(string tenantId);
}