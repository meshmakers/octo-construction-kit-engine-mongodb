namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

public class CkTypeDirectedAggregations
{
    public CkTypeAggregations In { get; set; } = null!;
    public CkTypeAggregations Out { get; set; } = null!;
}