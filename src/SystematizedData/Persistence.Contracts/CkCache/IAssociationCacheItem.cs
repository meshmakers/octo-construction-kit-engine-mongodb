using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface IAssociationCacheItem
{
    string Name { get; }
    CkId<CkAssociationId> RoleId { get; set; }
    Multiplicities InboundMultiplicity { get; set; }
    Multiplicities OutboundMultiplicity { get; set; }
    IEnumerable<IEntityCacheItem> AllowedTypes { get; set; }
}