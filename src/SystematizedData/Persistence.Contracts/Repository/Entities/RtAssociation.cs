using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class RtAssociation : IRtAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId AssociationId { get; set; }

    public OctoObjectId OriginRtId { get; set; }

    public string OriginCkId { get; set; } = null!;

    public OctoObjectId TargetRtId { get; set; }

    public string TargetCkId { get; set; }= null!;

    public string AssociationRoleId { get; set; }= null!;
}
