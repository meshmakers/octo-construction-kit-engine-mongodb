using System.Collections.Generic;
using System.Diagnostics;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(RoleId) + "}")]
public class AssociationCacheItem : IAssociationCacheItem
{
    public string Name { get; set; }
    public string RoleId { get; set; }

    public Multiplicities InboundMultiplicity { get; set; }

    public Multiplicities OutboundMultiplicity { get; set; }

    public IEnumerable<IEntityCacheItem> AllowedTypes { get; set; }
}
