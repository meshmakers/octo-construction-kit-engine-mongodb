using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

[DebuggerDisplay("{" + nameof(AssociationId) + "} -> {" + nameof(TargetCkTypeId) + "}")]
public class CkTypeAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId AssociationId { get; set; }

    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Returns the corresponding role Id
    /// </summary>
    public CkId<CkAssociationRoleId> RoleId { get; set; } = null!;

    public CkId<CkTypeId> OriginCkTypeId { get; set; } = null!;

    public CkId<CkTypeId> TargetCkTypeId { get; set; } = null!;

    public ICollection<CkId<CkAttributeId>>? TargetAttributes { get; set; }
}