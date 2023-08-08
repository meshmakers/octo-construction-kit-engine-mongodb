using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(AssociationId) + "} -> {" + nameof(TargetCkId) + "}")]
public class CkEntityAssociation 
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId AssociationId { get; set; }
    
    /// <summary>
    /// Returns the corresponding role Id
    /// </summary>
    public CkId<CkAssociationRoleId> RoleId { get; set; }

    public CkId<CkTypeId> OriginCkId { get; set; }

    public CkId<CkTypeId> TargetCkId { get; set; }
}
