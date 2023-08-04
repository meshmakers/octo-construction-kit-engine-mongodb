using System.Text.Json.Serialization;
using Persistence.Contracts;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtAttribute
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public CkId<CkAttributeId> Id { get; set; } 

    [JsonPropertyName("value")] public object? Value { get; set; }
}
