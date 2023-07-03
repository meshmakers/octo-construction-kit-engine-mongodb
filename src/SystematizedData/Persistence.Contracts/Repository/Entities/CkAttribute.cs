using System.Diagnostics;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkAttribute : ICkAttribute
{
    public string AttributeId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public AttributeValueTypes AttributeValueType { get; set; }

    public object DefaultValue { get; set; }

    public ICollection<object> DefaultValues { get; set; }

    public ICollection<ICkSelectionValue> SelectionValues { get; set; }
}
