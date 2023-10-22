using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface ICkDatabaseContext
{
    IDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    IDatabaseCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    IDatabaseCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    IDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    IDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    IDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    IDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    IDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    IDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }
    
    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);
}