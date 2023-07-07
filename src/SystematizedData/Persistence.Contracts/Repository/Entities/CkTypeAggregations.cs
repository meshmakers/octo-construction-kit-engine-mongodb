
namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeAggregations : ICkTypeAggregations
{
    public ICollection<ICkEntityAssociation>? Owned { get; set; }
    public ICollection<ICkEntityAssociation>? Inherited { get; set; }
}