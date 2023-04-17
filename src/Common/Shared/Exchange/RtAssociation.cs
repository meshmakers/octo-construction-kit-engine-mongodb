using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class RtAssociation
{
    [JsonProperty("roleId")] public string? RoleId { get; set; }

    [JsonProperty("targetRtId")]
    [JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public OctoObjectId TargetRtId { get; set; }

    [JsonProperty("targetCkId")] public string? TargetCkId { get; set; }
}
