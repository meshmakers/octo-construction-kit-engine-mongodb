using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using CkAttribute = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAttribute;
using CkEntity = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntity;
using CkEntityAssociation = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntityAssociation;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class TransientCkModel
{
    public TransientCkModel(CkModelId modelId)
    {
        ModelId = modelId;
        CkEntityAssociations = new List<CkEntityAssociation>();
        CkEntityInheritances = new List<CkEntityInheritance>();
        CkEntities = new List<CkEntity>();
        CkAttributes = new List<CkAttribute>();
        CkDependencies = new List<CkModelDependency>();
    }
    
    public CkModelId ModelId { get; }
    
    public List<CkModelDependency> CkDependencies { get; }

    public List<CkEntityAssociation> CkEntityAssociations { get; }
    public List<CkEntityInheritance> CkEntityInheritances { get; }
    public List<CkEntity> CkEntities { get; }
    public List<CkAttribute> CkAttributes { get; }
}
