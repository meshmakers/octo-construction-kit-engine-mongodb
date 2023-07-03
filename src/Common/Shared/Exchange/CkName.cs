using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkName
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("version")] public string? Version { get; set; }

}