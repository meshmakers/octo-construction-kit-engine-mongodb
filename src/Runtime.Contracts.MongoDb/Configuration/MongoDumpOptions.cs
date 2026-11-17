namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

public class MongoDumpOptions
{
    /// <summary>
    ///     Collections of a tenant database that hold platform-only data and never go into a tenant dump. The
    ///     secret defaults (<c>System.Communication/SecretDefault</c>, its own collection root) are projected into a
    ///     tenant by the communication controller, which projects them again after a restore.
    /// </summary>
    public static IReadOnlyList<string> PlatformOnlyTenantCollections { get; } =
        ["RtEntity_SystemCommunicationSecretDefault"];

    // Database/Collection
    public required string Database { get; set; }
    public string? Collection { get; set; }

    /// <summary>
    ///     Collections of <see cref="Database" /> left out of the dump (one <c>--excludeCollection</c> each).
    /// </summary>
    public IReadOnlyList<string> ExcludeCollections { get; set; } = [];

    // Output
    public string? OutputDirectory { get; set; }
    public string? Archive { get; set; }

    // Options
    public bool Gzip { get; set; }
    public bool Pretty { get; set; }
    public bool Verbose { get; set; }

    public static MongoDumpOptions ForDatabase(string database, string outputPath)
    {
        return new MongoDumpOptions
        {
            Database = database,
            OutputDirectory = outputPath
        };
    }

    public static MongoDumpOptions ForArchive(string database, string archivePath)
    {
        return new MongoDumpOptions
        {
            Database = database,
            Archive = archivePath,
            Gzip = true
        };
    }

    /// <summary>
    ///     The dump of a tenant database into an archive, without <see cref="PlatformOnlyTenantCollections" />.
    /// </summary>
    public static MongoDumpOptions ForTenantArchive(string database, string archivePath)
    {
        var options = ForArchive(database, archivePath);
        options.ExcludeCollections = PlatformOnlyTenantCollections;
        return options;
    }
}
