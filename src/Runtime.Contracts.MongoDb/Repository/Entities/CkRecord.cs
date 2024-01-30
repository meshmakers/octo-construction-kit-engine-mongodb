using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

/// <summary>
///     Represents a definition of a construction kit record in database
/// </summary>
[DebuggerDisplay("{" + nameof(CkRecordId) + "}")]
public class CkRecord
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkRecord()
    {
        Attributes = new HashSet<CkTypeAttribute>();
    }

    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkRecordId> CkRecordId { get; set; } = null!;

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
}