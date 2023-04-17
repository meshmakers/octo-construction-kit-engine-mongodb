namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

/// <summary>
///     The configuration for MongoDB.
/// </summary>
public class MongoConnectionOptions
{
    public MongoConnectionOptions()
    {
        MongoDbHost = "localhost:27017";
        DatabaseName = "admin";
        AuthenticationSource = "admin";

        UseTls = false;
        AllowInsecureTls = true;
    }

    public string MongoDbHost { get; set; }
    public string MongoDbUsername { get; set; }
    public string MongoDbPassword { get; set; }
    public string DatabaseName { get; set; }
    public string AuthenticationSource { get; set; }

    public bool UseTls { get; set; }

    public bool AllowInsecureTls { get; set; }
}
