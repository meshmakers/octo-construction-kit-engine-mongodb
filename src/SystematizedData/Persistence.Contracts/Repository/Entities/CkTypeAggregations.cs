
namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeAggregations
{
    public ICollection<CkEntityAssociation>? Owned { get; set; }
    public ICollection<CkEntityAssociation>? Inherited { get; set; }
}