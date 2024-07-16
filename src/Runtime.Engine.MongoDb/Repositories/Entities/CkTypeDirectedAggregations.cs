using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

public class CkTypeDirectedAggregations
{
    public CkTypeAggregations In { get; set; } = null!;
    public CkTypeAggregations Out { get; set; } = null!;
}