using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkModelRoot
{
    public CkModelRoot()
    {
        CkDependencies = new List<CkDependency>();
        CkEntities = new List<CkEntity>();
        CkAssociationRoles = new List<CkAssociationRole>();
        CkAttributes = new List<CkAttribute>();
    }
    
    [JsonRequired]
    public CkName? Name { get; set; }
    
    [JsonProperty("dependencies")] public List<CkDependency> CkDependencies { get; }

    [JsonProperty("entities")] public List<CkEntity> CkEntities { get; }

    [JsonProperty("associationRoles")] public List<CkAssociationRole> CkAssociationRoles { get; }

    [JsonProperty("attributes")] public List<CkAttribute> CkAttributes { get; }
}
