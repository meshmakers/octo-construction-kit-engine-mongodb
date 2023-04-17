using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class GraphQlDto
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? UserContext { get; set; }
}
