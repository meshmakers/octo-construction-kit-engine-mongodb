namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Simplified, JSON-serializable representation of a CkType for comparison purposes.
///     This model extracts key information from CkTypeGraph without complex dictionary keys.
/// </summary>
public class CkTypeInfo
{
    /// <summary>
    ///     The CkType identifier
    /// </summary>
    public string CkTypeId { get; set; } = null!;

    /// <summary>
    ///     Whether the type is final (cannot be inherited from)
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    ///     Whether the type is abstract (cannot be instantiated)
    /// </summary>
    public bool IsAbstract { get; set; }

    /// <summary>
    ///     Description of the type
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Whether this type is a collection root
    /// </summary>
    public bool IsCollectionRoot { get; set; }

    /// <summary>
    ///     Whether this type is a stream type
    /// </summary>
    public bool IsStreamType { get; set; }

    /// <summary>
    ///     The CkTypeId this type is derived from (if any)
    /// </summary>
    public string? DerivedFromCkTypeId { get; set; }

    /// <summary>
    ///     List of attribute IDs (as strings) that belong to this type
    /// </summary>
    public List<string> AttributeIds { get; set; } = new();

    /// <summary>
    ///     Number of incoming associations
    /// </summary>
    public int IncomingAssociationsCount { get; set; }

    /// <summary>
    ///     Number of outgoing associations
    /// </summary>
    public int OutgoingAssociationsCount { get; set; }

    /// <summary>
    ///     Number of indexes defined on this type
    /// </summary>
    public int IndexesCount { get; set; }
}
