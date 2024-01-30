using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

/// <summary>
///     Represents a definition of a construction kit type
/// </summary>
[DebuggerDisplay("{" + nameof(CkTypeId) + "}")]
public class CkType
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkType()
    {
        Attributes = new HashSet<CkTypeAttribute>();
        Indexes = new HashSet<CkTypeIndex>();
    }

    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; set; } = null!;

    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    public bool IsAbstract { get; set; }

    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    public ICollection<CkTypeAttribute> Attributes { get; set; }

    /// <summary>
    ///     Gets or sets a list of defined indexes
    /// </summary>
    public ICollection<CkTypeIndex>? Indexes { get; set; }

    /// <summary>
    ///     Gets or sets if the change stream should include pre and post images
    /// </summary>
    public bool EnableChangeStreamPreAndPostImages { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether this type is a collection root. When
    ///     true this type creates a collection in the database.
    /// </summary>
    public bool IsCollectionRoot { get; set; }
}