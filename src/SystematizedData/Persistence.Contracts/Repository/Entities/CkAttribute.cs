using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkAttribute : ICkAttribute
{
    public CkId<CkAttributeId> AttributeId { get; set; }

    public AttributeValueTypes AttributeValueType { get; set; }

    public object? DefaultValue { get; set; }

    public ICollection<object>? DefaultValues { get; set; }

    public ICollection<ICkSelectionValue>? SelectionValues { get; set; }
}