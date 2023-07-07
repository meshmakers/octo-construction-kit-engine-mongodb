using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkEntityAttribute
{
    [JsonProperty("id")] [JsonRequired] public string AttributeId { get; set; } = null!;

    [JsonProperty("name")] [JsonRequired] public string AttributeName { get; set; } = null!;

    [JsonProperty("isAutoCompleteEnabled")]
    public bool IsAutoCompleteEnabled { get; set; }

    [JsonProperty("autoCompleteFilter")] public string? AutoCompleteFilter { get; set; }

    [JsonProperty("autoCompleteLimit")] public int? AutoCompleteLimit { get; set; }

    [JsonProperty("autoIncrementReference")]
    public string? AutoIncrementReference { get; set; }
}