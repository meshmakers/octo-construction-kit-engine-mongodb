using System.Text.Json.Serialization;
using Persistence.Contracts;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtAssociation
{
    [JsonPropertyName("roleId")]
    [JsonRequired]
    public string RoleId { get; set; } = null!;

    [JsonPropertyName("targetRtId")]
    [JsonRequired]
    public OctoObjectId TargetRtId { get; set; }
    
    [JsonPropertyName("targetCkId")] 
    [JsonRequired]
    public CkId<CkTypeId> TargetCkId { get; set; }
}
