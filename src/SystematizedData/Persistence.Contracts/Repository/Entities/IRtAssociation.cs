using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface IRtAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId AssociationId { get; set; }

    OctoObjectId OriginRtId { get; set; }
    string OriginCkId { get; set; }
    OctoObjectId TargetRtId { get; set; }
    string TargetCkId { get; set; }
    string AssociationRoleId { get; set; }
}