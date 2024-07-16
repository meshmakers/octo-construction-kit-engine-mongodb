using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

internal class RtAssociationWithDepth : RtAssociation
{
    public int Depth { get; set; }
}