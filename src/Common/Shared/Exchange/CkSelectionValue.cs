using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkSelectionValue
{
    [JsonProperty("key")] public int Key { get; set; }

    [JsonProperty("name")] public string? Name { get; set; }
}
