using System.Diagnostics;
using System.Text.Json.Serialization;
using Persistence.Contracts;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

[DebuggerDisplay("{" + nameof(RoleId) + "} -> {" + nameof(TargetCkId) + "}")]
public class CkEntityAssociation
{
    [JsonPropertyName("roleId")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdAssociationIdConverter))]
    public CkId<CkAssociationId> RoleId { get; set; }

    [JsonPropertyName("targetCkId")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdTypeIdConverter))]
    public CkId<CkTypeId> TargetCkId { get; set; }


}