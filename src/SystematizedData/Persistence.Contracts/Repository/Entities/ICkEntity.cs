using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntity
{
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    CkId<CkTypeId> CkId { get; set; }
    
    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    bool IsFinal { get; set; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    bool IsAbstract { get; set; }

    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    ICollection<CkEntityAttribute> Attributes { get; set; }

    /// <summary>
    ///     Gets or sets a list of defined indexes
    /// </summary>
    ICollection<CkEntityIndex>? Indexes { get; set; }

    /// <summary>
    /// Gets or sets if the change stream should include pre and post images
    /// </summary>
    bool EnableChangeStreamPreAndPostImages { get; set; }
}