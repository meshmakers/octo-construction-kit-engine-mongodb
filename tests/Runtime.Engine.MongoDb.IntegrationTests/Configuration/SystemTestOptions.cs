// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
public class SystemTestOptions
{
    public string TenantId { get; set; } = null!;

    public string MongoDbImage { get; set; } = "mongo:8.0.15";
    public string AdminUser { get; set; } = "octo-system-admin";
    public string AdminUserPassword { get; set; } = null!;
    public string DatabaseUserPassword { get; set; } = null!;

    public bool UseDirectConnection { get; set; }

    /// <summary>
    /// If true, uses a local MongoDB instance instead of Testcontainers.
    /// Can also be set via environment variable USE_LOCAL_MONGODB=true
    /// </summary>
    public bool UseLocalDatabase { get; set; }

    /// <summary>
    /// The host:port of the local MongoDB instance (e.g. "localhost:27017")
    /// </summary>
    public string LocalDatabaseHost { get; set; } = "localhost:27017";
}
