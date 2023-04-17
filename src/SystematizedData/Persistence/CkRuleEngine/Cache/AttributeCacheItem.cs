using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class AttributeCacheItem
{
    public AttributeCacheItem(string attributeName, CkEntityAttribute ckEntityAttribute, CkAttribute ckAttribute)
    {
        AttributeName = attributeName;
        AttributeId = ckAttribute.AttributeId;
        AttributeValueType = ckAttribute.AttributeValueType;
        DefaultValue = ckAttribute.DefaultValue;
        DefaultValues = ckAttribute.DefaultValues?.ToList();
        SelectionValues = ckAttribute.SelectionValues?.ToList();
        IsAutoCompleteEnabled = ckEntityAttribute.IsAutoCompleteEnabled;
        AutoCompleteFilter = ckEntityAttribute.AutoCompleteFilter;
        AutoCompleteLimit = ckEntityAttribute.AutoCompleteLimit;
        AutoCompleteTexts = ckEntityAttribute.AutoCompleteTexts;
        AutoIncrementReference = ckEntityAttribute.AutoIncrementReference;
        ScopeId = ckAttribute.ScopeId;
    }

    public string AttributeName { get; }
    public string AttributeId { get; }

    public bool IsAutoCompleteEnabled { get; }

    public string AutoCompleteFilter { get; }

    public int AutoCompleteLimit { get; }

    public string AutoIncrementReference { get; }

    public ICollection<string> AutoCompleteTexts { get; }

    public ScopeIds ScopeId { get; set; }

    public AttributeValueTypes AttributeValueType { get; }

    public object DefaultValue { get; }

    public ICollection<object> DefaultValues { get; }
    public ICollection<CkSelectionValue> SelectionValues { get; }
}
