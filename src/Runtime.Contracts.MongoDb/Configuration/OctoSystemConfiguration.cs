using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

public class OctoSystemConfiguration
{
    private string _systemDatabaseName;
    private string _systemTenantId;

    public OctoSystemConfiguration(string databaseHost)
        : this()
    {
        DatabaseHost = databaseHost;
    }

    public OctoSystemConfiguration()
    {
        DatabaseHost = "localhost:27017";
        _systemTenantId = "OctoSystem";
        _systemDatabaseName = "OctoSystem";
        DatabaseUser = "octo-system-ds-user-{0}";
        AdminUser = "octo-system-admin";
        AuthenticationDatabaseName = "admin";
        UseTls = false;
        AllowInsecureTls = true;
    }

    public string DatabaseHost { get; set; }

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

    public string DatabaseUser { get; set; }
    public string? DatabaseUserPassword { get; set; }
    public string AdminUser { get; set; }
    public string? AdminUserPassword { get; set; }

    public string AuthenticationDatabaseName { get; set; }

    public bool UseTls { get; set; }

    public bool AllowInsecureTls { get; set; }
}