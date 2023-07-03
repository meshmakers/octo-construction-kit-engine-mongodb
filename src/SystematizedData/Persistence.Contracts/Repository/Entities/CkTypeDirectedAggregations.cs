namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeDirectedAggregations : ICkTypeDirectedAggregations
{
    public ICkTypeAggregations In { get; set; }
    public ICkTypeAggregations Out { get; set; }
}