namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

public class CkIndexFields
{
    public int? Weight { get; set; }

    public ICollection<string> AttributeNames { get; set; } = null!;
}