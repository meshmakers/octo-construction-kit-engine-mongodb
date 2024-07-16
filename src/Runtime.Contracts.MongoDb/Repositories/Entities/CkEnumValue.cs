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
    ///     A optional description of the enum value
    /// </summary>
    public string? Description { get; set; }
}