using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface ICkDatabaseContext
{
    ICkDatabaseCollection<CkModel> CkModels { get; }
    ICkDatabaseCollection<CkType> CkTypes { get; }
    ICkDatabaseCollection<CkAttribute> CkAttributes { get; }
    ICkDatabaseCollection<CkEnum> CkEnums { get; }
    ICkDatabaseCollection<CkRecord> CkRecords { get; }
    ICkDatabaseCollection<CkAssociationRole> CkAssociationRoles { get; }
    ICkDatabaseCollection<CkTypeAssociation> CkTypeAssociations { get; }
    ICkDatabaseCollection<CkTypeInheritance> CkTypeInheritances { get; }
    ICkDatabaseCollection<CkRecordInheritance> CkRecordInheritances { get; }
    
    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);
}