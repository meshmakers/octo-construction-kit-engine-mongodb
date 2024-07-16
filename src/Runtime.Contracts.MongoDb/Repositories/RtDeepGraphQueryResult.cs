using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
/// Represents the result of a runtime deep query.
/// </summary>
public class RtDeepGraphQueryResult
{
    /// <summary>
    ///     Gets or sets the identifier of the runtime entity
    /// </summary>
    public RtEntityId Id { get; init; } 
    
    /// <summary>
    ///     Gets or sets corresponding associations
    /// </summary>
    public IEnumerable<RtDeepGraphAssociationQueryResult> Associations { get; init; } = null!;

}