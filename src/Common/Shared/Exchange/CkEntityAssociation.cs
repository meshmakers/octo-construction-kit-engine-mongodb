using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkEntityAssociation
{
    [JsonProperty("roleId")]
    [JsonRequired]
    public string RoleId { get; set; } = null!;

    [JsonProperty("targetCkId")]
    [JsonRequired]
    public CkTypeId TargetCkId { get; set; } = null!;

    [JsonProperty("inboundMultiplicity")]
    [JsonConverter(typeof(StringEnumConverter))]
    [JsonRequired]
    public Multiplicities InboundMultiplicity { get; set; }

    [JsonProperty("outboundMultiplicity")]
    [JsonConverter(typeof(StringEnumConverter))]
    [JsonRequired]
    public Multiplicities OutboundMultiplicity { get; set; }
}