using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityAttribute
{
    string AttributeId { get; set; }
    string AttributeName { get; set; }
    bool IsAutoCompleteEnabled { get; set; }
    string? AutoCompleteFilter { get; set; }
    int? AutoCompleteLimit { get; set; }
    string? AutoIncrementReference { get; set; }
    ICollection<string>? AutoCompleteTexts { get; set; }
}