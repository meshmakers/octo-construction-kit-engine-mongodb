using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkAssociationRole
{
    [JsonProperty("id")] [JsonRequired] public string RoleId { get; set; } = null!;

    /// <summary>
    ///     Name of the association for inbound references (e. g. Children)
    /// </summary>
    [JsonProperty("inboundName")]
    [JsonRequired]
    public string InboundName { get; set; } = null!;

    /// <summary>
    ///     Name of the association for outbound references (e. g. Parent)
    /// </summary>
    [JsonProperty("outboundName")]
    [JsonRequired]
    public string OutboundName { get; set; } = null!;
}