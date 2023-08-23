using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface IAssociationCacheItem
{
    string Name { get; }
    CkId<CkAssociationRoleId> RoleId { get; set; }
    Multiplicities InboundMultiplicity { get; set; }
    Multiplicities OutboundMultiplicity { get; set; }
    IEnumerable<IEntityCacheItem> AllowedTypes { get; set; }
}