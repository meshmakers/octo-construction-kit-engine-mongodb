using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

[DebuggerDisplay("{" + nameof(AttributeId) + "}")]
public class CkAttribute
{
    [JsonPropertyName("id")] [JsonRequired] public CkAttributeId AttributeId { get; set; }

    [JsonPropertyName("valueType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AttributeValueTypes ValueType { get; set; }

    [JsonPropertyName("defaultValue")] public object? DefaultValue { get; set; }

    [JsonPropertyName("defaultValues")] public ICollection<object>? DefaultValues { get; set; }

    [JsonPropertyName("selectionValues")] public ICollection<CkSelectionValue>? SelectionValues { get; set; }
}