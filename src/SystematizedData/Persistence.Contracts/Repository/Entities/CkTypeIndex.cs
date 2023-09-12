namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeIndex 
{
    public IndexTypes IndexType { get; set; }

    public string? Language { get; set; }

    public ICollection<CkIndexFields> Fields { get; set; } = null!;
}
