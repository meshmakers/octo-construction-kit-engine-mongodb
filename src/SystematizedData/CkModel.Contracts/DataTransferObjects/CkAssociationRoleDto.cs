using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(AssociationRoleId) + "}")]
public class CkAssociationRoleDto
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public CkAssociationRoleId AssociationRoleId { get; set; }

    /// <summary>
    ///     Name of the association for inbound references (e. g. Children)
    /// </summary>
    [JsonPropertyName("inboundName")]
    [JsonRequired]
    public string InboundName { get; set; } = null!;

    /// <summary>
    ///     Name of the association for outbound references (e. g. Parent)
    /// </summary>
    [JsonPropertyName("outboundName")]
    [JsonRequired]
    public string OutboundName { get; set; } = null!;
    
    /// <summary>
    ///     Multiplicity of the inbound association
    /// </summary>
    [JsonPropertyName("inboundMultiplicity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonRequired]
    public MultiplicitiesDto InboundMultiplicity { get; set; }

    /// <summary>
    ///     Multiplicity of the outbound association
    /// </summary>
    [JsonPropertyName("outboundMultiplicity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonRequired]
    public MultiplicitiesDto OutboundMultiplicity { get; set; }
}