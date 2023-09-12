using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using CkAssociationRole = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAssociationRole;
using CkAttribute = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAttribute;
using CkModel = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class TransientCkModel
{
    public TransientCkModel(CkModel ckModel)
    {
        CkModel = ckModel;
        CkTypeAssociations = new List<CkTypeAssociation>();
        CkTypeInheritances = new List<CkTypeInheritance>();
        CkRecordInheritances = new List<CkRecordInheritance>();
        CkTypes = new List<CkType>();
        CkRecords = new List<CkRecord>();
        CkEnums = new List<CkEnum>();
        CkAttributes = new List<CkAttribute>();
        CkAssociationRoles = new List<CkAssociationRole>();
    }
    
    public CkModel CkModel { get; }

    public List<CkTypeAssociation> CkTypeAssociations { get; }
    public List<CkTypeInheritance> CkTypeInheritances { get; }
    public List<CkRecordInheritance> CkRecordInheritances { get; }
    public List<CkType> CkTypes { get; }
    public List<CkRecord> CkRecords { get; }
    public List<CkEnum> CkEnums { get; }
    public List<CkAttribute> CkAttributes { get; }
    public List<CkAssociationRole> CkAssociationRoles { get; }
}
