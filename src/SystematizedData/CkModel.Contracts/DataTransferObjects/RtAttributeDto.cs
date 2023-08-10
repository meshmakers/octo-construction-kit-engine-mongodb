using System.Text.Json.Serialization;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

public class RtAttributeDto
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public CkId<CkAttributeId> Id { get; set; } 

    [JsonPropertyName("value")] public object? Value { get; set; }
}
