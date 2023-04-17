using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkEntityIndexDto
{
    public CkEntityIndexDto()
    {
        Fields = new List<CkIndexFields>();
    }

    [JsonProperty("indexType")]
    [JsonConverter(typeof(StringEnumConverter))]
    public IndexTypeDto IndexType { get; set; }

    [JsonProperty("language")] public string? Language { get; set; }

    [JsonProperty("fields")] public List<CkIndexFields> Fields { get; }
}
