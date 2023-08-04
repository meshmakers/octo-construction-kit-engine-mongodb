using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface IRtAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId AssociationId { get; set; }

    OctoObjectId OriginRtId { get; set; }
    CkId<CkTypeId> OriginCkId { get; set; }
    OctoObjectId TargetRtId { get; set; }
    CkId<CkTypeId> TargetCkId { get; set; }
    CkId<CkAssociationId> AssociationRoleId { get; set; }
}