namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Type of difference detected between source and target entities
/// </summary>
public enum DifferenceType
{
    /// <summary>
    ///     Property was added in target (not present in source)
    /// </summary>
    Added,

    /// <summary>
    ///     Property was removed in target (present in source but not in target)
    /// </summary>
    Removed,

    /// <summary>
    ///     Property value was modified between source and target
    /// </summary>
    Modified,
}
