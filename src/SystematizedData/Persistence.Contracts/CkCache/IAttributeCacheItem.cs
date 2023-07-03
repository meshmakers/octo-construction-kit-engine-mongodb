using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface IAttributeCacheItem
{
    string AttributeName { get; }
    string AttributeId { get; }
    bool IsAutoCompleteEnabled { get; }
    string AutoCompleteFilter { get; }
    int AutoCompleteLimit { get; }
    string AutoIncrementReference { get; }
    ICollection<string> AutoCompleteTexts { get; }
    ScopeIds ScopeId { get; set; }
    AttributeValueTypes AttributeValueType { get; }
    object? DefaultValue { get; }
    ICollection<object>? DefaultValues { get; }
    ICollection<ICkSelectionValue>? SelectionValues { get; }
}