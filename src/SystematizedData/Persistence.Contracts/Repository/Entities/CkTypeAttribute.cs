using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkTypeAttribute 
{
    public CkId<CkAttributeId> AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    public ICollection<object>? AutoCompleteValues { get; set; }
    public string? AutoIncrementReference { get; set; }
    
    /// <summary>
    /// If true, the attribute is optional, that means it can be null
    /// </summary>
    public bool IsOptional { get; set; }
}
