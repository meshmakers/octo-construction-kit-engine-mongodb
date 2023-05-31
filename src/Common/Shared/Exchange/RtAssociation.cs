using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtAssociation
{
    [JsonProperty("roleId", Required = Required.Always)]
    [JsonRequired]
    public string RoleId { get; set; } = null!;

    [JsonProperty("targetRtId", Required = Required.Always)]
    [JsonRequired]
    public OctoObjectId TargetRtId { get; set; }
    
    [JsonProperty("targetCkId", Required = Required.Always)] 
    [JsonRequired]
    public string TargetCkId { get; set; } = null!;
}
