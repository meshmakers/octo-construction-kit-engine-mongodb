namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeDirectedAggregations
{
    public CkTypeAggregations In { get; set; } = null!;
    public CkTypeAggregations Out { get; set; } = null!;
}