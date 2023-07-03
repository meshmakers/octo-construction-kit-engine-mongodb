namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkTypeDirectedAggregations
{
    ICkTypeAggregations In { get; set; }
    ICkTypeAggregations Out { get; set; }
}