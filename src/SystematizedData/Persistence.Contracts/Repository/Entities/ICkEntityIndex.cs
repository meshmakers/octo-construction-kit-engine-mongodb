namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityIndex
{
    IndexTypes IndexType { get; set; }
    string Language { get; set; }
    ICollection<CkIndexFields> Fields { get; set; }
}