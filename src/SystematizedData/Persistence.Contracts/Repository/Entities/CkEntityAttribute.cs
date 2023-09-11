using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkEntityAttribute 
{
    public CkId<CkAttributeId> AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    public ICollection<object>? AutoCompleteValues { get; set; }
    public string? AutoIncrementReference { get; set; }
}
