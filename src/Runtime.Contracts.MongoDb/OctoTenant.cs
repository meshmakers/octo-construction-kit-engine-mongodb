namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Represents an Octo data source
/// </summary>
public class OctoTenant
{
    public OctoTenant(string tenantId, string databaseName)
        : this(tenantId, databaseName, null)
    {
    }

    public OctoTenant(string tenantId, string databaseName, string? parentTenantId)
    {
        TenantId = tenantId;
        DatabaseName = databaseName;
        ParentTenantId = parentTenantId;
    }

    public string TenantId { get; }
    public string DatabaseName { get; }

    /// <summary>
    ///     Id of the tenant's parent tenant. Null on registry records written before
    ///     the parent id existed — such a record lives in its parent's own database,
    ///     so a null still means "direct child of the registry owner" (AB#5151).
    /// </summary>
    public string? ParentTenantId { get; }
}