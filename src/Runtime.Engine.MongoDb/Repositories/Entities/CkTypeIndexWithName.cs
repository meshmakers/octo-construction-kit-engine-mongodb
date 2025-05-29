using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

public class CkTypeIndexWithName : CkTypeIndex
{
    public required string Name { get; set; }
}
