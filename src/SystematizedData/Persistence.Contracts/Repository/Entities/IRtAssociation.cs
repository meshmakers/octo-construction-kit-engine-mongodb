using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface IRtAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId AssociationId { get; set; }

    OctoObjectId OriginRtId { get; set; }
    CkTypeId OriginCkId { get; set; }
    OctoObjectId TargetRtId { get; set; }
    CkTypeId TargetCkId { get; set; }
    string AssociationRoleId { get; set; }
}