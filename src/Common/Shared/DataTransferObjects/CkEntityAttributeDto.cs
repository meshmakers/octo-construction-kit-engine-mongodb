using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkEntityAttributeDto
{
    public CkId<CkAttributeId> CkAttributeId { get; init; }
    public string AttributeName { get; init; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AttributeValueTypesDto AttributeValueType { get; init; }

    public bool IsAutoCompleteEnabled { get; init; }

    public string? AutoCompleteFilter { get; init; }

    public int AutoCompleteLimit { get; init; }

    public string? AutoIncrementReference { get; init; }

    public  IReadOnlyCollection<object>? AutoCompleteValues { get; init; }

    public CkAttributeDto? Attribute { get; init; }
}