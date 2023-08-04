using CkAssociationRole = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAssociationRole;
using CkAttribute = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAttribute;
using CkEntity = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntity;
using CkEntityAssociation = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntityAssociation;
using CkEntityInheritance = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntityInheritance;
using CkModel = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class TransientCkModel
{
    public TransientCkModel(DatabaseEntities.CkModel ckModel)
    {
        CkModel = ckModel;
        CkEntityAssociations = new List<CkEntityAssociation>();
        CkEntityInheritances = new List<CkEntityInheritance>();
        CkEntities = new List<CkEntity>();
        CkAttributes = new List<CkAttribute>();
        CkAssociationRoles = new List<CkAssociationRole>();
    }
    
    public DatabaseEntities.CkModel CkModel { get; }
    

    public List<CkEntityAssociation> CkEntityAssociations { get; }
    public List<CkEntityInheritance> CkEntityInheritances { get; }
    public List<CkEntity> CkEntities { get; }
    public List<CkAttribute> CkAttributes { get; }
    public List<CkAssociationRole> CkAssociationRoles { get; }
}
