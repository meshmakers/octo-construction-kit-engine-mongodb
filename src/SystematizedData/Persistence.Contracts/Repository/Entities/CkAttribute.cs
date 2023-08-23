using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkAttribute 
{
    public CkId<CkAttributeId> AttributeId { get; set; }

    public AttributeValueTypes AttributeValueType { get; set; }

    public ICollection<object>? DefaultValues { get; set; }

    public ICollection<CkSelectionValue>? SelectionValues { get; set; }
}