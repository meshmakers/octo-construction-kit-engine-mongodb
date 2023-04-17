using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkEntityAssociation
{
    [JsonProperty("roleId")] public string? RoleId { get; set; }

    [JsonProperty("targetCkId")] public string? TargetCkId { get; set; }

    [JsonProperty("inboundMultiplicity")]
    [JsonConverter(typeof(StringEnumConverter))]
    public Multiplicities InboundMultiplicity { get; set; }

    [JsonProperty("outboundMultiplicity")]
    [JsonConverter(typeof(StringEnumConverter))]
    public Multiplicities OutboundMultiplicity { get; set; }
}
