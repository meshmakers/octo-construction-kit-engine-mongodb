using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

/// <summary>
///     Represents a definition of a construction kit enum
/// </summary>
[DebuggerDisplay("{" + nameof(CkEnumId) + "}")]
public class CkEnum
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkEnum()
    {
        Values = new HashSet<CkEnumValue>();
    }
    
    /// <summary>
    ///     Gets or sets the construction kit model id 
    /// </summary>
    public CkModelId CkModelId { get; set; }

    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkEnumId> CkEnumId { get; set; }

    /// <summary>
    /// When true the enum is handles as flags enum
    /// </summary>
    public bool UseFlags { get; set; }

    /// <summary>
    ///     Gets or sets a list of enum values
    /// </summary>
    public ICollection<CkEnumValue> Values { get; set; }
}