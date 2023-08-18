using System.Diagnostics;
using System.Text.Json.Serialization;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(RoleId) + "} -> {" + nameof(TargetCkTypeId) + "}")]
public class CkTypeAssociationDto
{
    [JsonPropertyName("roleId")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdAssociationIdConverter))]
    public CkId<CkAssociationRoleId> RoleId { get; set; }

    [JsonPropertyName("targetCkTypeId")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdTypeIdConverter))]
    public CkId<CkTypeId> TargetCkTypeId { get; set; }


}