namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

/// <summary>
/// Result of a collection cleanup operation.
/// </summary>
public class CollectionCleanupResult
{
    /// <summary>
    /// Collections that were successfully deleted.
    /// </summary>
    public List<string> DeletedCollections { get; init; } = [];

    /// <summary>
    /// Collections that were skipped because they contain documents.
    /// </summary>
    public List<CollectionSkipInfo> SkippedCollections { get; init; } = [];

    /// <summary>
    /// Collections that could not be matched to any CK type.
    /// </summary>
    public List<string> UnmatchedCollections { get; init; } = [];

    /// <summary>
    /// Total number of collections analyzed.
    /// </summary>
    public int TotalAnalyzed { get; set; }
}

/// <summary>
/// Information about a skipped collection.
/// </summary>
public class CollectionSkipInfo
{
    /// <summary>
    /// The name of the collection.
    /// </summary>
    public required string CollectionName { get; init; }

    /// <summary>
    /// The number of documents in the collection.
    /// </summary>
    public long DocumentCount { get; init; }

    /// <summary>
    /// The reason why the collection was skipped.
    /// </summary>
    public required string Reason { get; init; }
}
