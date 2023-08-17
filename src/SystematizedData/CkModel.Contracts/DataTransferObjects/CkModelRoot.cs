using System.Text.Json.Serialization;
using Meshmakers.Octo.Common.Shared;

// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

public class CkModelRoot
{
    public CkModelRoot()
    {
        CkDependencies = new List<CkModelId>();
        CkTypes = new List<CkTypeDto>();
        CkAssociationRoles = new List<CkAssociationRoleDto>();
        CkAttributes = new List<CkAttributeDto>();
    }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    [JsonRequired]
    [JsonPropertyName("modelId")]
    public CkModelId ModelId { get; set; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    [JsonPropertyName("dependencies")] public List<CkModelId>? CkDependencies { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("entities")] public List<CkTypeDto>? CkTypes { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("associationRoles")] public List<CkAssociationRoleDto>? CkAssociationRoles { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("attributes")] public List<CkAttributeDto>? CkAttributes { get; set; }
}