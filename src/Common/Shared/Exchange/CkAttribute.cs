using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkAttribute
{
    [JsonProperty("id")] public string? AttributeId { get; set; }

    [JsonProperty("valueType")]
    [JsonConverter(typeof(StringEnumConverter))]
    public AttributeValueTypes ValueType { get; set; }

    [JsonProperty("defaultValue")] public object? DefaultValue { get; set; }

    [JsonProperty("defaultValues")] public ICollection<object>? DefaultValues { get; set; }

    [JsonProperty("selectionValues")] public ICollection<CkSelectionValue>? SelectionValues { get; set; }
}
