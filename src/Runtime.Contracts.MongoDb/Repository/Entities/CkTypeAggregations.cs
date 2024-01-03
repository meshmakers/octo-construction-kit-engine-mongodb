namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

public class CkTypeAggregations
{
    public ICollection<CkTypeAssociation>? Owned { get; set; }
    public ICollection<CkTypeAssociation>? Inherited { get; set; }
}