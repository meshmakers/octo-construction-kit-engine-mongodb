using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Summary information about an association
/// </summary>
public class AssociationSummary
{
    /// <summary>
    ///     Association identifier
    /// </summary>
    public OctoObjectId AssociationId { get; set; }

    /// <summary>
    ///     Association role identifier
    /// </summary>
    public CkId<CkAssociationRoleId> RoleId { get; set; } = null!;

    /// <summary>
    ///     Origin entity runtime identifier
    /// </summary>
    public OctoObjectId OriginRtId { get; set; }

    /// <summary>
    ///     Origin entity CkType identifier
    /// </summary>
    public CkId<CkTypeId> OriginCkTypeId { get; set; } = null!;

    /// <summary>
    ///     Target entity runtime identifier
    /// </summary>
    public OctoObjectId TargetRtId { get; set; }

    /// <summary>
    ///     Target entity CkType identifier
    /// </summary>
    public CkId<CkTypeId> TargetCkTypeId { get; set; } = null!;
}
