using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

public class CkGraphAssociations
{
    public CkGraphAssociations()
    {
        Owned = new List<CkEntityAssociationDto>();
        Inherited = new List<CkEntityAssociationDto>();
    }
    
    public ICollection<CkEntityAssociationDto> Owned { get;  }
    public ICollection<CkEntityAssociationDto> Inherited { get;}
}