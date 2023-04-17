using System.Collections.Generic;
using System.Diagnostics;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(RoleId) + "}")]
public class AssociationCacheItem
{
    public string Name { get; set; }
    public string RoleId { get; set; }

    public Multiplicities InboundMultiplicity { get; set; }

    public Multiplicities OutboundMultiplicity { get; set; }

    public IEnumerable<EntityCacheItem> AllowedTypes { get; set; }
}
