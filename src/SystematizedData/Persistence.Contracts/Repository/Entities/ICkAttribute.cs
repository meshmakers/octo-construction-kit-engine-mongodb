using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkAttribute
{
    string AttributeId { get; set; }
    ScopeIds ScopeId { get; set; }
    AttributeValueTypes AttributeValueType { get; set; }
    object DefaultValue { get; set; }
    ICollection<object> DefaultValues { get; set; }
    ICollection<ICkSelectionValue> SelectionValues { get; set; }
}