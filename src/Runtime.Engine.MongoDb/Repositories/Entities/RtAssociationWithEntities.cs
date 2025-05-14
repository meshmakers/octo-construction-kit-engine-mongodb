using System.Diagnostics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

[DebuggerDisplay("{GetLocation()}")]
public class RtAssociationWithEntities : RtTypeWithAttributes
{
    public RtAssociationWithEntities()
    {
    }

    /// <summary>Constructor</summary>
    public RtAssociationWithEntities(IReadOnlyDictionary<string, object?> attributes)
        : base(attributes)
    {
    }

    /// <summary>
    /// Gets or sets the object id of the association
    /// </summary>
    public OctoObjectId AssociationId { get; set; }

    /// <summary>
    ///     Gets or sets the association role id of the association role
    /// </summary>
    public CkId<CkAssociationRoleId>? AssociationRoleId { get; set; }

    protected override string GetLocation()
    {
        return $"{this.AssociationRoleId}@{this.AssociationId}";
    }

    /// <summary>
    /// Returns the list of entities that are part of the association
    /// </summary>
    public List<RtEntity> Entities { get; set; } = new();
}
