namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

public class CkEnumValue
{
    /// <summary>
    ///     Key of the enum value.
    /// </summary>
    public int Key { get; set; }

    /// <summary>
    ///     Name of the enum value.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    ///     An optional description of the enum value
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    ///     Indicates that the current enum value is an extension to the original enum.
    /// </summary>
    public bool IsExtension { get; set; } = false;
}
