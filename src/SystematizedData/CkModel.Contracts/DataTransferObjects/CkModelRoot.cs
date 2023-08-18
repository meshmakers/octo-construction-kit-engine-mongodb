using System.Text.Json.Serialization;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

public class CkModelRoot : CkMetaDto
{
    public CkModelRoot()
    {
        CkTypes = new List<CkTypeDto>();
        CkAssociationRoles = new List<CkAssociationRoleDto>();
        CkAttributes = new List<CkAttributeDto>();
    }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("entities")] public List<CkTypeDto>? CkTypes { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("associationRoles")] public List<CkAssociationRoleDto>? CkAssociationRoles { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("attributes")] public List<CkAttributeDto>? CkAttributes { get; set; }
}