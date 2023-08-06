using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(RoleId) + "}")]
public class AssociationCacheItem : IAssociationCacheItem
{
    internal AssociationCacheItem(string name, CkId<CkAssociationId> roleId, Multiplicities inboundMultiplicity,
        Multiplicities outboundMultiplicity, IEnumerable<IEntityCacheItem> allowedTypes)
    {
        Name = name;
        RoleId = roleId;
        InboundMultiplicity = inboundMultiplicity;
        OutboundMultiplicity = outboundMultiplicity;
        AllowedTypes = allowedTypes;
    }

    public string Name { get; } 
    public CkId<CkAssociationId> RoleId { get; set; }

    public Multiplicities InboundMultiplicity { get; set; }

    public Multiplicities OutboundMultiplicity { get; set; }

    public IEnumerable<IEntityCacheItem> AllowedTypes { get; set; }
}
