using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

public class OctoSystemConfiguration()
{
    private string _systemDatabaseName = "OctoSystem";
    private string _systemTenantId = "OctoSystem";

    public OctoSystemConfiguration(string databaseHost)
        : this()
    {
        DatabaseHost = databaseHost;
    }

    public string DatabaseHost { get; set; } = "localhost:27017";

    public string SystemTenantId
    {
        get => _systemTenantId;
        set
        {
            if (value == null || !Regex.IsMatch(value, ContractConstants.RegexWithoutWhitespaces))
            {
                throw ConfigurationErrorException.InvalidConfigurationValue("SystemTenantId", value);
            }

            _systemTenantId = value;
        }
    }

    public string SystemDatabaseName
    {
        get => _systemDatabaseName;
        set
        {
            if (value == null || !Regex.IsMatch(value, ContractConstants.RegexWithoutWhitespaces))
            {
                throw ConfigurationErrorException.InvalidConfigurationValue("SystemDatabaseName", value);
            }

            _systemDatabaseName = value;
        }
    }

    public string DatabaseUser { get; set; } = "octo-system-ds-user-{0}";
    public string? DatabaseUserPassword { get; set; }
    public string AdminUser { get; set; } = "octo-system-admin";
    public string? AdminUserPassword { get; set; }

    public string AuthenticationDatabaseName { get; set; } = "admin";

    public bool UseTls { get; set; } = false;

    public bool AllowInsecureTls { get; set; } = true;
    
    /// <summary>
    /// When set to true, the MongoDB connection will be established directly to the database host, without using the other nodes in the replica set.
    /// </summary>
    public bool UseDirectConnection { get; set; } = false;

    /// <summary>
    /// The name of the MongoDB replica set. When set, the driver explicitly connects to the named replica set,
    /// which is required for multi-document transactions to work correctly with MongoDB.Driver v3.x.
    /// </summary>
    public string? ReplicaSetName { get; set; }
}