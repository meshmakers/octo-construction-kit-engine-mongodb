using System.Diagnostics;
using System.Text.Json.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using YamlDotNet.Serialization;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

[DebuggerDisplay("{" + nameof(CkRoleId) + "} -> {" + nameof(TargetCkTypeId) + "}")]
public class CkTypeAssociationDto
{
    [JsonPropertyName("id")]
    [YamlMember(Alias = "id")]
    [JsonRequired]
    [JsonConverter(typeof(CkIdAssociationIdConverter))]
    public CkId<CkAssociationRoleId> CkRoleId { get; set; }

    [JsonRequired]
    [JsonConverter(typeof(CkIdTypeIdConverter))]
    public CkId<CkTypeId> TargetCkTypeId { get; set; }


}