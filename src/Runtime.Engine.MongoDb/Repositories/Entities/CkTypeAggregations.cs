using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

public class CkTypeAggregations
{
    public ICollection<CkTypeAssociation>? Owned { get; set; }
    public ICollection<CkTypeAssociation>? Inherited { get; set; }
}