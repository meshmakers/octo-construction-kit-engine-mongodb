using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkSelectionValue
{
    [JsonProperty("key")][JsonRequired]  public int Key { get; set; }

    [JsonProperty("name")] [JsonRequired] public string Name { get; set; } = null!;
}
