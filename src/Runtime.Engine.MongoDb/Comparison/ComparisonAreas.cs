namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

/// <summary>
///     Flags enum for specifying which areas to include in tenant comparison
/// </summary>
[Flags]
public enum ComparisonAreas
{
    /// <summary>
    ///     No comparison areas
    /// </summary>
    None = 0,

    /// <summary>
    ///     Compare tenant metadata (database names, entity counts)
    /// </summary>
    Metadata = 1,

    /// <summary>
    ///     Compare Construction Kit models
    /// </summary>
    CkModels = 2,

    /// <summary>
    ///     Compare runtime entities
    /// </summary>
    RtEntities = 4,

    /// <summary>
    ///     Compare associations between entities
    /// </summary>
    Associations = 8,

    /// <summary>
    ///     Compare all areas
    /// </summary>
    All = Metadata | CkModels | RtEntities | Associations,
}
