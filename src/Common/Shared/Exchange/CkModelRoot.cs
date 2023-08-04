using System.Collections.Generic;
using System.Text.Json.Serialization;
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkModelRoot
{
    public CkModelRoot()
    {
        CkDependencies = new List<CkModelId>();
        CkEntities = new List<CkEntity>();
        CkAssociationRoles = new List<CkAssociationRole>();
        CkAttributes = new List<CkAttribute>();
    }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    [JsonRequired]
    [JsonPropertyName("modelId")]
    public CkModelId ModelId { get; set; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    [JsonPropertyName("dependencies")] public List<CkModelId> CkDependencies { get; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("entities")] public List<CkEntity> CkEntities { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("associationRoles")] public List<CkAssociationRole> CkAssociationRoles { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [JsonPropertyName("attributes")] public List<CkAttribute> CkAttributes { get; set; }
}