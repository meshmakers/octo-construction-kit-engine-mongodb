using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityAttribute
{
    CkId<CkAttributeId> AttributeId { get; set; }
    string AttributeName { get; set; }
    bool IsAutoCompleteEnabled { get; set; }
    string? AutoCompleteFilter { get; set; }
    int? AutoCompleteLimit { get; set; }
    string? AutoIncrementReference { get; set; }
    ICollection<string>? AutoCompleteTexts { get; set; }
}