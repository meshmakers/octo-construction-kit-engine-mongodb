using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkAttribute
{
    CkId<CkAttributeId> AttributeId { get; set; }
    AttributeValueTypes AttributeValueType { get; set; }
    object? DefaultValue { get; set; }
    ICollection<object>? DefaultValues { get; set; }
    ICollection<ICkSelectionValue>? SelectionValues { get; set; }
}