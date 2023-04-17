namespace Meshmakers.Octo.Backend.Persistence;

/// <summary>
///     Represents an Octo data source
/// </summary>
public class OctoTenant
{
    public OctoTenant(string tenantId, string databaseName)
    {
        TenantId = tenantId;
        DatabaseName = databaseName;
    }

    public string TenantId { get; }
    public string DatabaseName { get; }
}
