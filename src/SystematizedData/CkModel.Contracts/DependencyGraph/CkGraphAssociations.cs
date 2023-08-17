using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

public class CkGraphAssociations
{
    public CkGraphAssociations()
    {
        Owned = new List<CkTypeAssociationDto>();
        Inherited = new List<CkTypeAssociationDto>();
    }
    
    public ICollection<CkTypeAssociationDto> Owned { get;  }
    public ICollection<CkTypeAssociationDto> Inherited { get;}
}