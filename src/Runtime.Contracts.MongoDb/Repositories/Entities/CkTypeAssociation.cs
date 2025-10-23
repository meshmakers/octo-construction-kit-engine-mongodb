using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

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
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

    /// <summary>
    ///     Returns the corresponding role Id
    /// </summary>
    public CkId<CkAssociationRoleId> RoleId { get; set; } = null!;

    /// <summary>
    /// Gets or sets the object id of the origin ck type id.
    /// </summary>
    public CkId<CkTypeId> OriginCkTypeId { get; set; } = null!;

    /// <summary>
    /// Gets or sets the target ck type id.
    /// </summary>
    public CkId<CkTypeId> TargetCkTypeId { get; set; } = null!;

    /// <summary>
    /// Gets or sets a list of attributes of the target ck type id, that are referential integrity attributes
    /// </summary>
    public ICollection<CkId<CkAttributeId>>? TargetCkAttributeIds { get; set; }
}
