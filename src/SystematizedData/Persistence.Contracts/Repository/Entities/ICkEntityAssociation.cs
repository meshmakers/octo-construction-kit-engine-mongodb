using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId AssociationId { get; set; }
    
    /// <summary>
    /// Returns the corresponding role Id
    /// </summary>
    CkId<CkAssociationId> RoleId { get; set; }


    CkId<CkTypeId> OriginCkId { get; set; }
    
    CkId<CkTypeId> TargetCkId { get; set; }
}