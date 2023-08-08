using System.Collections.Generic;
using System.Text.Json.Serialization;
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkEntityIndexDto
{
    public CkEntityIndexDto()
    {
        Fields = new List<CkIndexFields>();
    }

    [JsonPropertyName("indexType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IndexTypeDto IndexType { get; set; }

    [JsonPropertyName("language")] public string? Language { get; set; }

    [JsonPropertyName("fields")] public List<CkIndexFields> Fields { get; set; }
}