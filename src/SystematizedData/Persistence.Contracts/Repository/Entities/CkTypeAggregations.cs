
namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeAggregations
{
    public ICollection<CkTypeAssociation>? Owned { get; set; }
    public ICollection<CkTypeAssociation>? Inherited { get; set; }
}