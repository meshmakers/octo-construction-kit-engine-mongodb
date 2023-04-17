using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkAssociationRole
{
    [JsonProperty("id")] public string? RoleId { get; set; }

    /// <summary>
    ///     Name of the association for inbound references (e. g. Children)
    /// </summary>
    [JsonProperty("inboundName")]
    public string? InboundName { get; set; }

    /// <summary>
    ///     Name of the association for outbound references (e. g. Parent)
    /// </summary>
    [JsonProperty("outboundName")]
    public string? OutboundName { get; set; }
}
