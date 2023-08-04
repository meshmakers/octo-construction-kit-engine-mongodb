using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class RtAssociation : IRtAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId AssociationId { get; set; }

    public OctoObjectId OriginRtId { get; set; }

    public CkId<CkTypeId> OriginCkId { get; set; }

    public OctoObjectId TargetRtId { get; set; }

    public CkId<CkTypeId> TargetCkId { get; set; }

    public CkId<CkAssociationId> AssociationRoleId { get; set; }
}
