using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class StatisticsDto
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [JsonRequired]
    public string AttributeName { get; set; } = null!;
    
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Value { get; set; }
}