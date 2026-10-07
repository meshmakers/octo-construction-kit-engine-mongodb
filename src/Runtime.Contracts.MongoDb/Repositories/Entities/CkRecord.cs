using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

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
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

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
    
    /// <summary>
    ///     An optional description of the record
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     The record's own key sub-attribute (<c>CkRecordDto.RecordKey</c>, AB#5528 concept §4.6):
    ///     names the sub-attribute that identifies an element of a record array, used to carry Secret
    ///     sub-values over when the array is replaced. <c>null</c> when the record declares none (an
    ///     inherited key is resolved by the CK graph, not stored here). Must round-trip through the
    ///     database, otherwise the runtime CK cache reads <c>null</c> (AB#5533, same failure class as
    ///     AB#4589).
    /// </summary>
    public string? RecordKey { get; set; }
}
