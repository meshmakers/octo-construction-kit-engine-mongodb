using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkEntityAttribute
{
    [JsonProperty("id")] public string? AttributeId { get; set; }

    [JsonProperty("name")] public string? AttributeName { get; set; }

    [JsonProperty("isAutoCompleteEnabled")]
    public bool IsAutoCompleteEnabled { get; set; }

    [JsonProperty("autoCompleteFilter")] public string? AutoCompleteFilter { get; set; }

    [JsonProperty("autoCompleteLimit")] public int AutoCompleteLimit { get; set; }

    [JsonProperty("autoIncrementReference")]
    public string? AutoIncrementReference { get; set; }
}
