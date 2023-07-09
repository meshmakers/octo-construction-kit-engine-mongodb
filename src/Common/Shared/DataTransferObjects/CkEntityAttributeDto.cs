using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkEntityAttributeDto
{
    public string? AttributeId { get; set; }
    public string? AttributeName { get; set; }

    [JsonConverter(typeof(StringEnumConverter), typeof(ConstantCaseNamingStrategy))]
    public AttributeValueTypesDto AttributeValueType { get; set; }

    public bool IsAutoCompleteEnabled { get; set; }

    public string? AutoCompleteFilter { get; set; }

    public int AutoCompleteLimit { get; set; }

    public string? AutoIncrementReference { get; set; }

    public ICollection<string>? AutoCompleteTexts { get; set; }

    public CkAttributeDto? Attribute { get; set; }
}
