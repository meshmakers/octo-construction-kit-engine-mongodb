using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkDependency
{
    [JsonProperty("id")] public string? Id { get; set; }
    [JsonProperty("version")] public string? Version { get; set; }
}