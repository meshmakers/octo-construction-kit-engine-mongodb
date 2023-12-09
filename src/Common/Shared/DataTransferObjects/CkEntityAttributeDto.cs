using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkEntityAttributeDto
{
    public string? AttributeId { get; init; }
    public string? AttributeName { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AttributeValueTypesDto AttributeValueType { get; init; }

    public bool IsAutoCompleteEnabled { get; init; }

    public string? AutoCompleteFilter { get; init; }

    public int AutoCompleteLimit { get; init; }

    public string? AutoIncrementReference { get; init; }

    public ICollection<string>? AutoCompleteTexts { get; init; }

    public CkAttributeDto? Attribute { get; init; }
}