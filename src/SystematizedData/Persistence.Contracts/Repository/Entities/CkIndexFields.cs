namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkIndexFields 
{
    public int? Weight { get; set; }

    public ICollection<string> AttributeNames { get; set; } = null!;
}
