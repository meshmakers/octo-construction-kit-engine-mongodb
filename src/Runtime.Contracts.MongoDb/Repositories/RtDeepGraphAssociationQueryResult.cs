using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
/// Represents an association of a runtime entity in a deep query.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class RtDeepGraphAssociationQueryResult : RtTypeWithAttributes
{
    /// <summary>
    ///     Gets or sets the object id of the association
    /// </summary>
    public OctoObjectId AssociationId { get; init; } 
    
    /// <summary>
    ///     Gets or sets the association role id of the association role
    /// </summary>
    public CkId<CkAssociationRoleId>? AssociationRoleId { get; init; } 
    
    /// <summary>
    ///     Gets or sets the object id of the target runtime entity
    /// </summary>
    public OctoObjectId TargetRtId { get; init; } = default!;
    
    /// <summary>
    ///     Gets or sets the target ck type id.
    /// </summary>
    public CkId<CkTypeId>? TargetCkTypeId { get; init; } 

    /// <inheritdoc />
    protected override string GetLocation()
    {
        return $"{AssociationRoleId}@{AssociationId}";
    }
}