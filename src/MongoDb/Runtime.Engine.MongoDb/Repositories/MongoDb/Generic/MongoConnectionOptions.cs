// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     The configuration for MongoDB.
/// </summary>
public class MongoConnectionOptions
{
    // ReSharper disable once ConvertConstructorToMemberInitializers
    public MongoConnectionOptions()
    {
        MongoDbHost = "localhost:27017";
        DatabaseName = "admin";
        AuthenticationSource = "admin";

        UseTls = false;
        AllowInsecureTls = true;
    }

    public string MongoDbHost { get; set; }
    public string MongoDbUsername { get; set; } = null!;
    public string? MongoDbPassword { get; set; }
    public string DatabaseName { get; set; }
    public string AuthenticationSource { get; set; }

    public bool UseTls { get; set; }

    public bool AllowInsecureTls { get; set; }
}