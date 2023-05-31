using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtAttribute
{
    [JsonProperty("id", Required = Required.Always)]
    [JsonRequired]
    public string? Id { get; set; }

    [JsonProperty("value")] public object? Value { get; set; }
}
