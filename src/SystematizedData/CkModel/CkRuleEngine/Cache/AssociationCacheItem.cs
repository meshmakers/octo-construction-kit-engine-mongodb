using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(RoleId) + "}")]
public class AssociationCacheItem : IAssociationCacheItem
{
    public string Name { get; set; }
    public CkId<CkAssociationId> RoleId { get; set; }

    public Multiplicities InboundMultiplicity { get; set; }

    public Multiplicities OutboundMultiplicity { get; set; }

    public IEnumerable<IEntityCacheItem> AllowedTypes { get; set; }
}
