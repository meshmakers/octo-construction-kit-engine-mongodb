using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface IAssociationCacheItem
{
    string Name { get; set; }
    string RoleId { get; set; }
    Multiplicities InboundMultiplicity { get; set; }
    Multiplicities OutboundMultiplicity { get; set; }
    IEnumerable<IEntityCacheItem> AllowedTypes { get; set; }
}