using System.Diagnostics;
using System.Text.Json.Serialization;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkAttributeDto
{
    [JsonPropertyName("id")] [JsonRequired]
    public CkAttributeId AttributeId { get; set; }

    [JsonPropertyName("valueType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AttributeValueTypesDto ValueType { get; set; }

    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; set; }

    [JsonPropertyName("defaultValues")]
    public ICollection<object>? DefaultValues { get; set; }

    [JsonPropertyName("selectionValues")]
    public ICollection<CkSelectionValueDto>? SelectionValues { get; set; }
}