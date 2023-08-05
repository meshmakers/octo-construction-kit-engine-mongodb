using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

/// <summary>
///     Represents a definition of a construction kit type
/// </summary>
[DebuggerDisplay("{" + nameof(CkId) + "}")]
public class CkEntity 
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkEntity()
    {
        Attributes = new HashSet<CkEntityAttribute>();
        Indexes = new HashSet<CkEntityIndex>();
    }
    
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkId { get; set; }

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
    public ICollection<CkEntityAttribute> Attributes { get; set; }

    /// <summary>
    ///     Gets or sets a list of defined indexes
    /// </summary>
    public ICollection<CkEntityIndex>? Indexes { get; set; }
    
    /// <summary>
    /// Gets or sets if the change stream should include pre and post images
    /// </summary>
    public bool EnableChangeStreamPreAndPostImages { get; set; }
}
