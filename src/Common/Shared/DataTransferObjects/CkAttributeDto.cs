using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class CkAttributeDto
{
    public string? AttributeId { get; set; }

    public ScopeIdsDto ScopeId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AttributeValueTypesDto AttributeValueType { get; set; }

    public object? DefaultValue { get; set; }

    public ICollection<object>? DefaultValues { get; set; }

    public ICollection<CkSelectionValueDto>? SelectionValues { get; set; }
}