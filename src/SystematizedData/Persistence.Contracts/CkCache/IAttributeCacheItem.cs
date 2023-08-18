using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface IAttributeCacheItem
{
    string AttributeName { get; }
    CkId<CkAttributeId> AttributeId { get; }
    bool IsAutoCompleteEnabled { get; }
    string? AutoCompleteFilter { get; }
    int? AutoCompleteLimit { get; }
    string? AutoIncrementReference { get; }
    ICollection<string>? AutoCompleteTexts { get; }
    AttributeValueTypes AttributeValueType { get; }
    object? DefaultValue { get; }
    ICollection<object>? DefaultValues { get; }
    ICollection<CkSelectionValue>? SelectionValues { get; }
}