using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class AttributeCacheItem : IAttributeCacheItem
{
    public AttributeCacheItem(string attributeName, CkEntityAttribute ckEntityAttribute, CkAttribute ckAttribute)
    {
        AttributeName = attributeName;
        AttributeId = ckAttribute.AttributeId;
        AttributeValueType = ckAttribute.AttributeValueType;
        DefaultValues = ckAttribute.DefaultValues?.ToList();
        SelectionValues = ckAttribute.SelectionValues?.ToList();
        IsAutoCompleteEnabled = ckEntityAttribute.IsAutoCompleteEnabled;
        AutoCompleteFilter = ckEntityAttribute.AutoCompleteFilter;
        AutoCompleteLimit = ckEntityAttribute.AutoCompleteLimit;
        AutoCompleteTexts = ckEntityAttribute.AutoCompleteTexts;
        AutoIncrementReference = ckEntityAttribute.AutoIncrementReference;
    }

    public string AttributeName { get; }
    public CkId<CkAttributeId> AttributeId { get; }

    public bool IsAutoCompleteEnabled { get; }

    public string? AutoCompleteFilter { get; }

    public int? AutoCompleteLimit { get; }

    public string? AutoIncrementReference { get; }

    public ICollection<string>? AutoCompleteTexts { get; }

    public AttributeValueTypes AttributeValueType { get; }

    public ICollection<object>? DefaultValues { get; }
    public ICollection<CkSelectionValue>? SelectionValues { get; }
}
