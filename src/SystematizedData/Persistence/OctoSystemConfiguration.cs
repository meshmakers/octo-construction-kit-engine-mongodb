using System.Text.RegularExpressions;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class OctoSystemConfiguration
{
    private string _systemDatabaseName;

    public OctoSystemConfiguration(string databaseHost)
        : this()
    {
        DatabaseHost = databaseHost;
    }

    public OctoSystemConfiguration()
    {
        DatabaseHost = "localhost:27017";
        SystemDatabaseName = "OctoSystem";
        DatabaseUser = "octo-system-ds-user-{0}";
        AdminUser = "octo-system-admin";
        AuthenticationDatabaseName = "admin";
        UseTls = false;
        AllowInsecureTls = true;
    }

    public string DatabaseHost { get; set; }

    public string SystemDatabaseName
    {
        get => _systemDatabaseName;
        set
        {
            if (value == null || !Regex.IsMatch(value, Constants.RegexWithoutWhitespaces))
            {
                throw new ConfigurationErrorException(
                    "Impossible to apply MongoDB name setting: value is absent, empty or contains whitespaces."
                );
            }

            _systemDatabaseName = value;
        }
    }

    public string DatabaseUser { get; set; }
    public string DatabaseUserPassword { get; set; }
    public string AdminUser { get; set; }
    public string AdminUserPassword { get; set; }

    public string AuthenticationDatabaseName { get; set; }

    public bool UseTls { get; set; }

    public bool AllowInsecureTls { get; set; }
}
