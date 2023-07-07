using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkModelRoot
{
    public CkModelRoot()
    {
        CkDependencies = new List<CkModelDependency>();
        CkEntities = new List<CkEntity>();
        CkAssociationRoles = new List<CkAssociationRole>();
        CkAttributes = new List<CkAttribute>();
    }
    
    [JsonRequired]
    public CkModelId ModelId { get; set; } = null!;
    
    [JsonProperty("dependencies")] public List<CkModelDependency> CkDependencies { get; }

    [JsonProperty("entities")] public List<CkEntity> CkEntities { get; }

    [JsonProperty("associationRoles")] public List<CkAssociationRole> CkAssociationRoles { get; }

    [JsonProperty("attributes")] public List<CkAttribute> CkAttributes { get; }
}
