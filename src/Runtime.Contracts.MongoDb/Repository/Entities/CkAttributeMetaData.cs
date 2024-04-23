namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

/// <summary>
/// Represents metadata of an construction kit attribute in the database
/// </summary>
public class CkAttributeMetaData
{
    /// <summary>
    ///     Metadata key
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>
    ///     Metadata value
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    ///     An optional description of the attribute
    /// </summary>
    public string? Description { get; set; }
}