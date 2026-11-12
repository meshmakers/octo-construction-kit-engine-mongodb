namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

public class MongoRestoreOptions
{
    // Database/Collection
    public required string Database { get; set; }
    public string Collection { get; set; } = "*";
    
    // Namespace mapping for restore
    public string? NsFrom { get; set; }
    public string? NsTo { get; set; }

    /// <summary>
    /// Namespaces of the archive that are never restored (<c>--nsExclude</c>, one per pattern; wildcards allowed,
    /// matched against the archive's namespaces before any <see cref="NsFrom" />/<see cref="NsTo" /> mapping). With
    /// <see cref="Drop" />, an excluded collection of the target is neither dropped nor overwritten. The tenant
    /// restore excludes the ownership markers this way (AB#8672).
    /// </summary>
    public IReadOnlyList<string> NsExclude { get; set; } = [];
    
    // Input
    public string? InputDirectory { get; set; }
    public string? Archive { get; set; }

    // Options
    public bool Drop { get; set; }
    public bool Gzip { get; set; }
    public bool Verbose { get; set; }
    public bool DryRun { get; set; }
    public bool OplogReplay { get; set; }
    public bool RestoreDbUsersAndRoles { get; set; }
    public int? NumParallelCollections { get; set; }

    /// <summary>
    /// Documents per bulk insert (<c>--batchSize</c>). <c>null</c> uses
    /// <see cref="OctoSystemConfiguration.MongoRestoreBatchSize"/> (AB#6981); the tool is never left to its own default.
    /// </summary>
    public int? BatchSize { get; set; }

    public static MongoRestoreOptions FromArchive(string archivePath, string database)
    {
        return new MongoRestoreOptions
        {
            Archive = archivePath,
            Database = database,
            Gzip = true
        };
    }
}
